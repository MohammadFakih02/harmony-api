using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Harmony.API.Grains;

/// <summary>
/// Grain-memory implementation of <see cref="IUserGrain"/> — the D1 replacement for the Redis
/// presence keys (<c>session:{id}</c> ZSET / <c>user:{id}:status|preferred|idle|statusmsg</c>)
/// and the global <c>presence:online</c> sweep. The connection set lives in this activation's
/// memory; because the grain is single-threaded, the "first connection → online" and "last
/// connection → offline" transitions are computed without locks or a liveness ZSET.
///
/// <para><b>Layering:</b> the grain injects the singleton <see cref="IHubBroadcaster"/> directly
/// (it wraps the singleton <c>IHubContext</c>) but must resolve the scoped EF repositories through
/// an <see cref="IServiceScopeFactory"/> — a grain outlives any request scope, so a scoped
/// DbContext cannot be constructor-injected. A scope is opened per broadcast/load and disposed
/// immediately.</para>
///
/// <para><b>Fail-open:</b> like the Redis service, a repository hiccup never throws into the hub
/// connection lifecycle — audience resolution and the Postgres load default to empty/"online"
/// on error rather than propagating.</para>
///
/// <para>The status-resolution rule and the friend/guild fan-out shape are deliberately kept
/// byte-identical to <see cref="Harmony.Infrastructure.Redis.RedisPresenceService"/> (still the
/// Test-environment implementation), so behaviour is unchanged across the swap.</para>
/// </summary>
public sealed class UserGrain : Grain, IUserGrain
{
    // A connection whose last heartbeat is older than this is a ghost (two missed 45s beats) —
    // its OnDisconnectedAsync never fired (e.g. an abrupt drop SignalR hasn't timed out yet).
    private static readonly TimeSpan ConnectionLiveness = TimeSpan.FromSeconds(90);

    // The prune cadence — the grain-local equivalent of the retired PresenceSweepService. Only
    // fires work when a connection has gone stale; an online user's grain is kept alive by its
    // 45s heartbeats, so the timer sees fresh timestamps and does nothing.
    private static readonly TimeSpan PrunePeriod = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<UserGrain> _logger;

    // connectionId → last-seen unix seconds. In-memory; lost (correctly) on deactivation/restart.
    private readonly Dictionary<string, long> _connections = [];

    private string _preferred = PresenceStatus.Online;
    private bool _idle;
    private string? _statusMessage;
    private bool _loaded; // whether preferred/message have been hydrated from Postgres

    private long UserId => this.GetPrimaryKeyLong();

    public UserGrain(
        IServiceScopeFactory scopeFactory,
        IHubBroadcaster broadcaster,
        ILogger<UserGrain> logger
    )
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Grain-local prune: the sweep is per-activation now, not a global scan. KeepAlive=false so
        // an offline user's grain (no heartbeats) is still free to idle-collect; an online user's
        // grain stays resident on its heartbeats. Non-interleaving — it runs as a normal grain turn.
        this.RegisterGrainTimer(
            static (grain, ct) => grain.PruneTickAsync(),
            this,
            new GrainTimerCreationOptions
            {
                DueTime = PrunePeriod,
                Period = PrunePeriod,
                Interleave = false,
                KeepAlive = false,
            }
        );
        return base.OnActivateAsync(cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------------------------

    public async Task SetOnline(string connectionId)
    {
        RemoveStale();
        var wasEmpty = _connections.Count == 0;
        _connections[connectionId] = Now();

        if (!wasEmpty)
            return; // already had another tab/device open — no status change

        await EnsureLoadedAsync();

        // Suppress the online broadcast for invisible users — friends must not see them come
        // online. Their own tabs learn the real status via the StatusChanged self-broadcast.
        if (_preferred == PresenceStatus.Invisible)
            return;

        var effective = ResolveEffective(_preferred, _idle);
        await FanOutAsync(
            new OnlineStatusPayload(UserId, effective, _statusMessage),
            (recipientId, p) => _broadcaster.BroadcastOnlineStatusAsync(recipientId, p),
            (guildId, p) => _broadcaster.BroadcastOnlineStatusToGuildAsync(guildId, p)
        );
    }

    public async Task SetOffline(string connectionId)
    {
        RemoveStale();
        _connections.Remove(connectionId);
        if (_connections.Count == 0)
            await MarkOfflineAndBroadcastAsync();
    }

    public Task Heartbeat(string connectionId)
    {
        // Just refresh this connection's liveness score — no load, no broadcast. Re-adds the
        // entry if a prune had dropped it (a brief network stall that recovered).
        _connections[connectionId] = Now();
        return Task.CompletedTask;
    }

    public Task<bool> IsConnected()
    {
        RemoveStale();
        return Task.FromResult(_connections.Count > 0);
    }

    // -------------------------------------------------------------------------
    // Status reads / writes
    // -------------------------------------------------------------------------

    public async Task<string> GetStatus()
    {
        RemoveStale();
        if (_connections.Count == 0)
            return PresenceStatus.Offline; // disconnected → offline, no Postgres load needed
        await EnsureLoadedAsync();
        return ResolveEffective(_preferred, _idle);
    }

    public async Task SetPreferredStatus(string preferred)
    {
        if (!PresenceStatus.IsValidPreferred(preferred))
            preferred = PresenceStatus.Online; // defensive — the endpoint validator already gates this

        await EnsureLoadedAsync(); // load first so we don't clobber the cached custom message
        _preferred = preferred;

        RemoveStale();
        if (_connections.Count == 0)
            return; // don't manufacture presence for a disconnected user

        var effective = ResolveEffective(_preferred, _idle);
        // Friends see the masked effective value; the user's own tabs see the real preferred value
        // so the picker stays in sync (incl. invisible/dnd).
        await BroadcastStatusChangedAsync(friendsStatus: effective, selfStatus: preferred);
    }

    public async Task<string> GetPreferredStatus()
    {
        await EnsureLoadedAsync();
        return _preferred;
    }

    public async Task SetIdle(bool idle)
    {
        _idle = idle;

        await EnsureLoadedAsync();
        // Idle only shifts the effective status on plain "online" (online ↔ away). A manual
        // away/dnd/invisible choice is unaffected — nothing to recompute or broadcast.
        if (_preferred != PresenceStatus.Online)
            return;

        RemoveStale();
        if (_connections.Count == 0)
            return;

        var effective = ResolveEffective(_preferred, idle);
        // Auto-away is a derived state, not a masked choice — the user's own tabs reflect it too.
        await BroadcastStatusChangedAsync(friendsStatus: effective, selfStatus: effective);
    }

    public async Task SetCustomStatus(string? message)
    {
        var normalized = string.IsNullOrWhiteSpace(message) ? null : message.Trim();

        await EnsureLoadedAsync();
        _statusMessage = normalized;

        RemoveStale();
        if (_connections.Count == 0)
            return; // nobody's watching an offline user live

        await BroadcastStatusChangedAsync(
            friendsStatus: ResolveEffective(_preferred, _idle),
            selfStatus: _preferred
        );
    }

    public async Task<string?> GetStatusMessage()
    {
        await EnsureLoadedAsync();
        return _statusMessage;
    }

    // -------------------------------------------------------------------------
    // Prune timer
    // -------------------------------------------------------------------------

    /// <summary>
    /// Per-activation replacement for the old global sweep: drop connections whose heartbeat has
    /// lapsed and, if that emptied the set, broadcast offline. Catches the rare silent death of a
    /// last connection whose OnDisconnectedAsync never ran; the common disconnect path is still
    /// SignalR firing OnDisconnectedAsync → <see cref="SetOffline"/>.
    /// </summary>
    private async Task PruneTickAsync()
    {
        var hadConnections = _connections.Count > 0;
        RemoveStale();
        if (hadConnections && _connections.Count == 0)
            await MarkOfflineAndBroadcastAsync();
    }

    // -------------------------------------------------------------------------
    // Resolution + load
    // -------------------------------------------------------------------------

    /// <summary>
    /// The public effective status, assuming the user is connected — identical rule to
    /// RedisPresenceService.ResolveEffective. Invisible masks to offline for observers.
    /// </summary>
    private static string ResolveEffective(string preferred, bool idle) =>
        preferred switch
        {
            PresenceStatus.Invisible => PresenceStatus.Offline,
            PresenceStatus.Dnd => PresenceStatus.Dnd,
            PresenceStatus.Away => PresenceStatus.Away,
            _ => idle ? PresenceStatus.Away : PresenceStatus.Online, // online (+ idle → away)
        };

    /// <summary>
    /// Hydrates preferred status + custom message from Postgres once per activation. Fails open:
    /// a DB hiccup leaves the defaults (online / no message) and marks loaded, so it never throws
    /// into a presence call and never hammers a down database — it re-tries on the next activation.
    /// </summary>
    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = await users.GetByIdAsync(UserId);
            _preferred = user?.PreferredStatus is { Length: > 0 } p ? p : PresenceStatus.Online;
            _statusMessage = string.IsNullOrEmpty(user?.StatusMessage) ? null : user!.StatusMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: failed loading durable status for user {UserId} — defaulting to online",
                UserId
            );
        }
    }

    private void RemoveStale()
    {
        if (_connections.Count == 0)
            return;
        var cutoff = Now() - (long)ConnectionLiveness.TotalSeconds;
        foreach (var (connId, seen) in _connections.ToArray())
        {
            if (seen <= cutoff)
                _connections.Remove(connId);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // -------------------------------------------------------------------------
    // Broadcast fan-out (friend + guild audiences, per-recipient try/catch)
    // -------------------------------------------------------------------------

    private async Task MarkOfflineAndBroadcastAsync()
    {
        _idle = false; // idle is meaningless once disconnected
        await FanOutAsync(
            new OfflineStatusPayload(UserId),
            (recipientId, p) => _broadcaster.BroadcastOfflineStatusAsync(recipientId, p),
            (guildId, p) => _broadcaster.BroadcastOfflineStatusToGuildAsync(guildId, p)
        );
    }

    /// <summary>
    /// Sends StatusChanged to friends + co-guild members (with <paramref name="friendsStatus"/>,
    /// custom message masked off when they appear offline) and to the user's own connections
    /// (with <paramref name="selfStatus"/> — keeps multi-tab pickers in sync).
    /// </summary>
    private async Task BroadcastStatusChangedAsync(string friendsStatus, string selfStatus)
    {
        var friendsMessage = friendsStatus == PresenceStatus.Offline ? null : _statusMessage;
        await FanOutAsync(
            new StatusChangedPayload(UserId, friendsStatus, friendsMessage),
            (recipientId, p) => _broadcaster.BroadcastStatusChangedAsync(recipientId, p),
            (guildId, p) => _broadcaster.BroadcastStatusChangedToGuildAsync(guildId, p)
        );

        try
        {
            await _broadcaster.BroadcastStatusChangedAsync(
                UserId,
                new StatusChangedPayload(UserId, selfStatus, _statusMessage)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: self StatusChanged broadcast failed for user {UserId} — continuing",
                UserId
            );
        }
    }

    /// <summary>
    /// Resolves the user's friends + guilds (one scope, fail-open to empty) and fans a payload out
    /// to each, one call per recipient, each individually try/caught so one dead connection can't
    /// abort the loop — the same shape as RedisPresenceService's fan-out.
    /// </summary>
    private async Task FanOutAsync<TPayload>(
        TPayload payload,
        Func<long, TPayload, Task> toFriend,
        Func<long, TPayload, Task> toGuild
    )
    {
        var (friendIds, guildIds) = await ResolveAudienceAsync();

        foreach (var friendId in friendIds)
        {
            try
            {
                await toFriend(friendId, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Presence: broadcast failed for recipient {RecipientId} of user {UserId} — continuing",
                    friendId,
                    UserId
                );
            }
        }

        foreach (var guildId in guildIds)
        {
            try
            {
                await toGuild(guildId, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Presence: guild broadcast failed for guild {GuildId} (user {UserId}) — continuing",
                    guildId,
                    UserId
                );
            }
        }
    }

    /// <summary>
    /// One scope, both lookups, each fails open independently (a DB hiccup → no recipients for that
    /// audience) so presence broadcasts never break the connection lifecycle.
    /// </summary>
    private async Task<(List<long> Friends, List<long> Guilds)> ResolveAudienceAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var friends = scope.ServiceProvider.GetRequiredService<IFriendRepository>();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

        List<long> friendIds = [];
        List<long> guildIds = [];

        try
        {
            friendIds = await friends.GetFriendIdsAsync(UserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: friend-id resolution failed for user {UserId} — broadcasting to no friends",
                UserId
            );
        }

        try
        {
            guildIds = await guilds.GetGuildIdsForUserAsync(UserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: guild-id resolution failed for user {UserId} — broadcasting to no guilds",
                UserId
            );
        }

        return (friendIds, guildIds);
    }
}
