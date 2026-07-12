using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="IPresenceService"/>. Tracks each user's active SignalR
/// connections in a ZSET scored by last-heartbeat (multi-tab/device aware; stale entries —
/// ghost ids left behind by an API restart or a dead socket — age out and are pruned on
/// every liveness check, so they can never hold a user "online" or suppress the
/// online/offline broadcasts) and resolves a public <em>effective</em>
/// status from three inputs:
///   • <b>preferred</b> — the user's durable choice (online/away/dnd/invisible), cached in
///     <c>user:{id}:preferred</c> with Postgres as the source of truth.
///   • <b>idle</b> — the client's 15-min inactivity flag, <c>user:{id}:idle</c>.
///   • <b>connected</b> — whether <c>session:{id}</c> is non-empty.
///
/// The resolved value is written to <c>user:{id}:status</c> (TTL 60s, heartbeat-refreshed)
/// and is what friends / <see cref="GetStatusAsync"/> read. An invisible user resolves to
/// <c>offline</c> for everyone else, while their own tabs sync the real preferred value.
///
/// Fails open throughout: if Redis is unavailable, presence is simply not tracked —
/// never throws into the hub connection lifecycle.
/// </summary>
public sealed class RedisPresenceService : IPresenceService
{
    private const string OnlineZSetKey = "presence:online";
    private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(60);

    // A connection with no heartbeat for this long is a ghost (two missed 45s beats).
    // Ghosts appear when OnDisconnectedAsync never fires for a connection id — an API
    // restart being the common case — and must not count toward "is this user connected".
    private static readonly TimeSpan ConnectionLiveness = TimeSpan.FromSeconds(90);

    private readonly IRedisConnectionProvider _redisProvider;
    private readonly IHubBroadcaster _broadcaster;
    private readonly IUserRepository _users;
    private readonly IFriendRepository _friends;
    private readonly IGuildRepository _guilds;
    private readonly ILogger<RedisPresenceService> _logger;

    public RedisPresenceService(
        IRedisConnectionProvider redisProvider,
        IHubBroadcaster broadcaster,
        IUserRepository users,
        IFriendRepository friends,
        IGuildRepository guilds,
        ILogger<RedisPresenceService> logger
    )
    {
        _redisProvider = redisProvider;
        _broadcaster = broadcaster;
        _users = users;
        _friends = friends;
        _guilds = guilds;
        _logger = logger;
    }

    public async Task SetOnlineAsync(
        long userId,
        string connectionId,
        CancellationToken ct = default
    )
    {
        if (!_redisProvider.IsConnected)
        {
            _logger.LogDebug(
                "Presence: Redis unavailable — skipping online tracking for user {UserId}",
                userId
            );
            return;
        }

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            // One-time migration: pre-liveness deployments stored sessions as a plain SET —
            // replace it rather than crash every ZSET op with WRONGTYPE.
            if (await db.KeyTypeAsync(SessionKey(userId)) == RedisType.Set)
                await db.KeyDeleteAsync(SessionKey(userId));
            await db.SortedSetAddAsync(
                SessionKey(userId),
                connectionId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );

            var preferred = await GetPreferredAsync(db, userId);
            var idle = await IsIdleAsync(db, userId);
            var effective = ResolveEffective(preferred, idle);
            // Warm the custom-message cache from Postgres so the member-list bulk read can
            // surface it while this user is online — and carry it on the online broadcast
            // below so observers see it the moment this user connects.
            var statusMessage = await ReadStatusMessageAsync(db, userId);

            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);
            await db.SortedSetAddAsync(
                OnlineZSetKey,
                userId.ToString(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );

            var connectionCount = await CountLiveConnectionsAsync(db, userId);
            if (connectionCount != 1)
                return; // already had another tab/device open — no status change

            // Suppress the online broadcast for invisible users — friends must not see
            // them come online. Their own tabs learn the real status via StatusChanged.
            if (preferred == PresenceStatus.Invisible)
                return;

            var onlinePayload = new OnlineStatusPayload(userId, effective, statusMessage);
            await BroadcastToFriendsAsync(
                userId,
                onlinePayload,
                (recipientId, payload) =>
                    _broadcaster.BroadcastOnlineStatusAsync(recipientId, payload, ct)
            );
            // Also reach co-guild members (not just friends) so member-list dots update live.
            await BroadcastToGuildsAsync(
                userId,
                onlinePayload,
                (guildId, payload) =>
                    _broadcaster.BroadcastOnlineStatusToGuildAsync(guildId, payload, ct)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: failed setting user {UserId} online — continuing",
                userId
            );
        }
    }

    public async Task SetOfflineAsync(
        long userId,
        string connectionId,
        CancellationToken ct = default
    )
    {
        if (!_redisProvider.IsConnected)
        {
            _logger.LogDebug(
                "Presence: Redis unavailable — skipping offline tracking for user {UserId}",
                userId
            );
            return;
        }

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            await db.SortedSetRemoveAsync(SessionKey(userId), connectionId);

            var remaining = await CountLiveConnectionsAsync(db, userId);
            if (remaining != 0)
                return; // other tabs/devices still connected — stay online

            await MarkOfflineAndBroadcastAsync(db, userId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: failed setting user {UserId} offline — continuing",
                userId
            );
        }
    }

    public async Task HeartbeatAsync(long userId, string connectionId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();

            // Refresh THIS connection's liveness score — the per-connection dead-man's switch
            // that lets ghost ids age out of the session ZSET.
            await db.SortedSetAddAsync(
                SessionKey(userId),
                connectionId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );

            // Recompute effective so a keep-alive can't clobber an away/dnd/invisible
            // state back to a literal "online".
            var preferred = await GetPreferredAsync(db, userId);
            var idle = await IsIdleAsync(db, userId);
            var effective = ResolveEffective(preferred, idle);

            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);
            // Keep the idle flag alive alongside the status key so a genuinely-idle-but-connected
            // user stays "away"; it only lapses (within StatusTtl) once heartbeats stop — i.e. a
            // crash — which is exactly what prevents a stuck idle key.
            if (idle)
                await db.KeyExpireAsync(IdleKey(userId), StatusTtl);
            await db.SortedSetAddAsync(
                OnlineZSetKey,
                userId.ToString(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: heartbeat failed for user {UserId} — ignoring", userId);
        }
    }

    public async Task SetPreferredStatusAsync(
        long userId,
        string preferred,
        CancellationToken ct = default
    )
    {
        if (!PresenceStatus.IsValidPreferred(preferred))
            preferred = PresenceStatus.Online; // defensive — the validator already gates the endpoint

        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            await db.StringSetAsync(PreferredKey(userId), preferred); // cache; Postgres is the truth

            // Only touch the public status key + broadcast if the user is actually
            // connected — otherwise we'd manufacture presence for an offline user.
            var connected = await CountLiveConnectionsAsync(db, userId) > 0;
            if (!connected)
                return;

            var idle = await IsIdleAsync(db, userId);
            var effective = ResolveEffective(preferred, idle);
            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);

            // Friends see the masked effective value; the user's own tabs see the real
            // preferred value so the picker stays in sync (incl. invisible/dnd). The custom
            // message rides along so a status change doesn't blank it on observers.
            var message = await ReadStatusMessageAsync(db, userId);
            await BroadcastStatusChangedAsync(
                userId,
                friendsStatus: effective,
                selfStatus: preferred,
                statusMessage: message,
                ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: failed setting preferred status for user {UserId} — continuing",
                userId
            );
        }
    }

    public async Task SetIdleAsync(long userId, bool idle, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();

            if (idle)
                // TTL so a crashed/dropped client can't leave a zombie idle flag behind: it
                // auto-expires within StatusTtl and is kept alive by HeartbeatAsync while the
                // client keeps talking (a dead-man's switch, like the status key itself).
                await db.StringSetAsync(IdleKey(userId), "1", StatusTtl);
            else
                await db.KeyDeleteAsync(IdleKey(userId));

            // Idle only shifts the effective status when the user is on plain "online"
            // (online ↔ away). A manual away/dnd/invisible choice is unaffected, so there's
            // nothing to recompute or broadcast.
            var preferred = await GetPreferredAsync(db, userId);
            if (preferred != PresenceStatus.Online)
                return;

            var connected = await CountLiveConnectionsAsync(db, userId) > 0;
            if (!connected)
                return;

            var effective = ResolveEffective(preferred, idle);
            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);

            // Auto-away is a derived state, not a masked choice — the user's own tabs
            // should reflect it too, so self and friends both get the effective value.
            var message = await ReadStatusMessageAsync(db, userId);
            await BroadcastStatusChangedAsync(
                userId,
                friendsStatus: effective,
                selfStatus: effective,
                statusMessage: message,
                ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: failed setting idle={Idle} for user {UserId} — continuing",
                idle,
                userId
            );
        }
    }

    public async Task<string> GetStatusAsync(long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return PresenceStatus.Offline;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var value = await db.StringGetAsync(StatusKey(userId));
            return value.IsNullOrEmpty ? PresenceStatus.Offline : value.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: failed reading status for user {UserId}", userId);
            return PresenceStatus.Offline;
        }
    }

    public async Task<bool> IsConnectedAsync(long userId, CancellationToken ct = default)
    {
        // Fails CLOSED for the push gate: an uncertain state reads as "connected" so the
        // caller skips the push rather than risk buzzing a user who is looking at the app.
        if (!_redisProvider.IsConnected)
            return true;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            return await CountLiveConnectionsAsync(db, userId) > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: failed reading session set for user {UserId}",
                userId
            );
            return true;
        }
    }

    public async Task<IReadOnlyDictionary<long, string>> GetStatusesAsync(
        IEnumerable<long> userIds,
        CancellationToken ct = default
    )
    {
        var ids = userIds.Distinct().ToList();
        var result = ids.ToDictionary(id => id, _ => PresenceStatus.Offline);

        if (ids.Count == 0 || !_redisProvider.IsConnected)
            return result;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var keys = ids.Select(id => (RedisKey)StatusKey(id)).ToArray();
            var values = await db.StringGetAsync(keys);

            for (var i = 0; i < ids.Count; i++)
            {
                if (!values[i].IsNullOrEmpty)
                    result[ids[i]] = values[i].ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: failed reading statuses (MGET) — returning offline defaults");
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<long, string?>> GetStatusMessagesAsync(
        IEnumerable<long> userIds,
        CancellationToken ct = default
    )
    {
        var ids = userIds.Distinct().ToList();
        var result = ids.ToDictionary(id => id, _ => (string?)null);

        if (ids.Count == 0 || !_redisProvider.IsConnected)
            return result;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var keys = ids.Select(id => (RedisKey)StatusMessageKey(id)).ToArray();
            var values = await db.StringGetAsync(keys);

            for (var i = 0; i < ids.Count; i++)
            {
                if (values[i].HasValue && values[i].ToString() is { Length: > 0 } s)
                    result[ids[i]] = s;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: failed reading status messages (MGET)");
        }

        return result;
    }

    public async Task SetCustomStatusAsync(
        long userId,
        string? message,
        CancellationToken ct = default
    )
    {
        var normalized = string.IsNullOrWhiteSpace(message) ? null : message.Trim();

        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            await db.StringSetAsync(StatusMessageKey(userId), normalized ?? ""); // empty = none

            // Only broadcast for a connected user — nobody's watching an offline one live.
            var connected = await CountLiveConnectionsAsync(db, userId) > 0;
            if (!connected)
                return;

            var preferred = await GetPreferredAsync(db, userId);
            var idle = await IsIdleAsync(db, userId);
            var effective = ResolveEffective(preferred, idle);

            await BroadcastStatusChangedAsync(
                userId,
                friendsStatus: effective,
                selfStatus: preferred,
                statusMessage: normalized,
                ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: failed setting custom status for user {UserId} — continuing",
                userId
            );
        }
    }

    public async Task<string> GetPreferredStatusAsync(long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return PresenceStatus.Online;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            return await GetPreferredAsync(db, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: failed reading preferred status for user {UserId}", userId);
            return PresenceStatus.Online;
        }
    }

    public async Task<int> SweepStaleAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return 0;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)staleThreshold.TotalSeconds;

            // Every member whose most-recent heartbeat score is at/below the cutoff is stale.
            // The score is refreshed by SetOnlineAsync/HeartbeatAsync, so a live client keeps
            // itself above the cutoff; only a crashed client or a restarted server lets it lapse.
            var stale = await db.SortedSetRangeByScoreAsync(
                OnlineZSetKey,
                double.NegativeInfinity,
                cutoff
            );
            if (stale.Length == 0)
                return 0;

            var reaped = 0;
            foreach (var member in stale)
            {
                if (!long.TryParse(member.ToString(), out var userId))
                {
                    // Defensive: drop an unparseable member so it can't wedge every future sweep.
                    await db.SortedSetRemoveAsync(OnlineZSetKey, member);
                    continue;
                }

                try
                {
                    await MarkOfflineAndBroadcastAsync(db, userId, ct);
                    reaped++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Presence: failed reaping stale user {UserId} — continuing",
                        userId
                    );
                }
            }

            if (reaped > 0)
                _logger.LogInformation("Presence: swept {Count} stale connection(s).", reaped);

            return reaped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: stale-connection sweep failed — continuing");
            return 0;
        }
    }

    // -------------------------------------------------------------------------
    // Resolution + reads
    // -------------------------------------------------------------------------

    /// <summary>
    /// The public effective status (what friends / GetStatusAsync see), assuming the
    /// user is connected. Callers only persist this while a connection exists; a
    /// disconnected user has no status key and reads as offline.
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
    /// Reads the cached preferred status, falling back to Postgres on a cache miss
    /// and repopulating the cache. Defaults to "online".
    /// </summary>
    private async Task<string> GetPreferredAsync(IDatabase db, long userId)
    {
        var cached = await db.StringGetAsync(PreferredKey(userId));
        if (!cached.IsNullOrEmpty)
            return cached.ToString();

        var user = await _users.GetByIdAsync(userId);
        var preferred = user?.PreferredStatus ?? PresenceStatus.Online;
        await db.StringSetAsync(PreferredKey(userId), preferred); // warm the cache
        return preferred;
    }

    private static async Task<bool> IsIdleAsync(IDatabase db, long userId) =>
        await db.KeyExistsAsync(IdleKey(userId));

    /// <summary>
    /// Reads the cached custom status message, falling back to Postgres on a cache miss
    /// and repopulating the cache (the message is stored as an empty string to mark
    /// "no message", distinct from a cold/missing key). Returns null when there's none.
    /// </summary>
    private async Task<string?> ReadStatusMessageAsync(IDatabase db, long userId)
    {
        var cached = await db.StringGetAsync(StatusMessageKey(userId));
        if (cached.HasValue)
            return cached.ToString() is { Length: > 0 } s ? s : null;

        var user = await _users.GetByIdAsync(userId);
        var message = string.IsNullOrEmpty(user?.StatusMessage) ? null : user!.StatusMessage;
        await db.StringSetAsync(StatusMessageKey(userId), message ?? ""); // warm (empty = none)
        return message;
    }

    // -------------------------------------------------------------------------
    // Broadcast fan-out
    // -------------------------------------------------------------------------

    /// <summary>
    /// Clears every presence key for a user and broadcasts OfflineStatus to their friends
    /// and co-guild members. Shared by the graceful last-disconnect path
    /// (<see cref="SetOfflineAsync"/>) and the crash-recovery sweep
    /// (<see cref="SweepStaleAsync"/>). Idempotent — deleting already-absent keys is a no-op,
    /// so on the graceful path the extra session-key delete is a no-op (the set is already
    /// empty), while on the sweep path it clears the lingering ghost connection ids.
    /// </summary>
    private async Task MarkOfflineAndBroadcastAsync(
        IDatabase db,
        long userId,
        CancellationToken ct
    )
    {
        await db.KeyDeleteAsync(SessionKey(userId)); // clear any ghost connection ids
        await db.KeyDeleteAsync(StatusKey(userId));
        await db.KeyDeleteAsync(IdleKey(userId)); // idle is meaningless once disconnected
        await db.SortedSetRemoveAsync(OnlineZSetKey, userId.ToString());

        var offlinePayload = new OfflineStatusPayload(userId);
        await BroadcastToFriendsAsync(
            userId,
            offlinePayload,
            (recipientId, payload) =>
                _broadcaster.BroadcastOfflineStatusAsync(recipientId, payload, ct)
        );
        // Also reach co-guild members so their member-list dots go grey live.
        await BroadcastToGuildsAsync(
            userId,
            offlinePayload,
            (guildId, payload) =>
                _broadcaster.BroadcastOfflineStatusToGuildAsync(guildId, payload, ct)
        );
    }

    /// <summary>
    /// Fans a payload out to the user's friends, one call per recipient, each
    /// individually try/caught so one bad push can't poison the loop. Mirrors
    /// RedisUnreadCountService's per-member fan-out.
    /// </summary>
    private async Task BroadcastToFriendsAsync<TPayload>(
        long userId,
        TPayload payload,
        Func<long, TPayload, Task> sendOne
    )
    {
        var recipientIds = await ResolveFriendIdsAsync(userId);
        foreach (var recipientId in recipientIds)
        {
            try
            {
                await sendOne(recipientId, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Presence: broadcast failed for recipient {RecipientId} of user {UserId} — continuing",
                    recipientId,
                    userId
                );
            }
        }
    }

    /// <summary>
    /// Fans a presence payload out to the group of every guild the user belongs to (one group
    /// broadcast per guild) so co-members — not just friends — see it live. Each call is individually
    /// try/caught; the guild-id lookup fails open (no recipients), never breaking the connection
    /// lifecycle. Invisible-masking is already applied by the caller.
    /// </summary>
    private async Task BroadcastToGuildsAsync<TPayload>(
        long userId,
        TPayload payload,
        Func<long, TPayload, Task> sendToGuild
    )
    {
        List<long> guildIds;
        try
        {
            guildIds = await _guilds.GetGuildIdsForUserAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: guild-id resolution failed for user {UserId} — broadcasting to no guilds",
                userId
            );
            return;
        }

        foreach (var guildId in guildIds)
        {
            try
            {
                await sendToGuild(guildId, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Presence: guild broadcast failed for guild {GuildId} (user {UserId}) — continuing",
                    guildId,
                    userId
                );
            }
        }
    }

    /// <summary>
    /// Sends StatusChanged to the user's friends (with <paramref name="friendsStatus"/>)
    /// and to the user's own connections (with <paramref name="selfStatus"/>) — the latter
    /// keeps multi-tab pickers in sync and is the only path with recipients today.
    /// </summary>
    private async Task BroadcastStatusChangedAsync(
        long userId,
        string friendsStatus,
        string selfStatus,
        string? statusMessage,
        CancellationToken ct
    )
    {
        // Friends never see the custom message of someone who appears offline (invisible).
        var friendsMessage = friendsStatus == PresenceStatus.Offline ? null : statusMessage;
        var maskedPayload = new StatusChangedPayload(userId, friendsStatus, friendsMessage);

        await BroadcastToFriendsAsync(
            userId,
            maskedPayload,
            (recipientId, payload) =>
                _broadcaster.BroadcastStatusChangedAsync(recipientId, payload, ct)
        );
        // Co-guild members get the same masked value (effective status + message, offline-masked for
        // invisible) so member-list dots and status lines track changes live, not only for friends.
        await BroadcastToGuildsAsync(
            userId,
            maskedPayload,
            (guildId, payload) => _broadcaster.BroadcastStatusChangedToGuildAsync(guildId, payload, ct)
        );

        try
        {
            await _broadcaster.BroadcastStatusChangedAsync(
                userId,
                new StatusChangedPayload(userId, selfStatus, statusMessage),
                ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: self StatusChanged broadcast failed for user {UserId} — continuing",
                userId
            );
        }
    }

    /// <summary>
    /// Friend-recipient seam — resolves the user's accepted friends so presence /
    /// status broadcasts reach them. Fails open (returns no recipients) if the lookup
    /// throws, so a DB hiccup never breaks the hub connection lifecycle.
    /// </summary>
    private async Task<List<long>> ResolveFriendIdsAsync(long userId)
    {
        try
        {
            return await _friends.GetFriendIdsAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Presence: friend-id resolution failed for user {UserId} — broadcasting to no friends",
                userId
            );
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // Key helpers
    // -------------------------------------------------------------------------

    public static string StatusKey(long userId) => $"user:{userId}:status";

    public static string SessionKey(long userId) => $"session:{userId}";

    /// <summary>
    /// Counts the user's LIVE connections, pruning ghosts first: any session entry whose
    /// last-heartbeat score is older than <see cref="ConnectionLiveness"/> is removed before
    /// counting. This is what makes logout-goes-offline reliable across API restarts — a
    /// ghost id (whose OnDisconnectedAsync never ran) would otherwise keep the count non-zero
    /// forever, suppressing the offline broadcast (and the next online one, per the ==1 gate).
    /// </summary>
    private static async Task<long> CountLiveConnectionsAsync(IDatabase db, long userId)
    {
        var cutoff =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)ConnectionLiveness.TotalSeconds;
        await db.SortedSetRemoveRangeByScoreAsync(
            SessionKey(userId),
            double.NegativeInfinity,
            cutoff
        );
        return await db.SortedSetLengthAsync(SessionKey(userId));
    }

    public static string PreferredKey(long userId) => $"user:{userId}:preferred";

    public static string IdleKey(long userId) => $"user:{userId}:idle";

    public static string StatusMessageKey(long userId) => $"user:{userId}:statusmsg";
}
