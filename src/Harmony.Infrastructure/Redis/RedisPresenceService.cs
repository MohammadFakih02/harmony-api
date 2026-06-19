using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="IPresenceService"/>. Tracks each user's active SignalR
/// connections in a SET (multi-tab/device aware) and resolves a public <em>effective</em>
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

    private readonly IRedisConnectionProvider _redisProvider;
    private readonly IHubBroadcaster _broadcaster;
    private readonly IUserRepository _users;
    private readonly ILogger<RedisPresenceService> _logger;

    public RedisPresenceService(
        IRedisConnectionProvider redisProvider,
        IHubBroadcaster broadcaster,
        IUserRepository users,
        ILogger<RedisPresenceService> logger
    )
    {
        _redisProvider = redisProvider;
        _broadcaster = broadcaster;
        _users = users;
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
            await db.SetAddAsync(SessionKey(userId), connectionId);

            var preferred = await GetPreferredAsync(db, userId);
            var idle = await IsIdleAsync(db, userId);
            var effective = ResolveEffective(preferred, idle);

            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);
            await db.SortedSetAddAsync(
                OnlineZSetKey,
                userId.ToString(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );

            var connectionCount = await db.SetLengthAsync(SessionKey(userId));
            if (connectionCount != 1)
                return; // already had another tab/device open — no status change

            // Suppress the online broadcast for invisible users — friends must not see
            // them come online. Their own tabs learn the real status via StatusChanged.
            if (preferred == PresenceStatus.Invisible)
                return;

            await BroadcastToFriendsAsync(
                userId,
                new OnlineStatusPayload(userId, effective),
                (recipientId, payload) =>
                    _broadcaster.BroadcastOnlineStatusAsync(recipientId, payload, ct)
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
            await db.SetRemoveAsync(SessionKey(userId), connectionId);

            var remaining = await db.SetLengthAsync(SessionKey(userId));
            if (remaining != 0)
                return; // other tabs/devices still connected — stay online

            await db.KeyDeleteAsync(StatusKey(userId));
            await db.KeyDeleteAsync(IdleKey(userId)); // idle is meaningless once disconnected
            await db.SortedSetRemoveAsync(OnlineZSetKey, userId.ToString());

            await BroadcastToFriendsAsync(
                userId,
                new OfflineStatusPayload(userId),
                (recipientId, payload) =>
                    _broadcaster.BroadcastOfflineStatusAsync(recipientId, payload, ct)
            );
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

    public async Task HeartbeatAsync(long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();

            // Recompute effective so a keep-alive can't clobber an away/dnd/invisible
            // state back to a literal "online".
            var preferred = await GetPreferredAsync(db, userId);
            var idle = await IsIdleAsync(db, userId);
            var effective = ResolveEffective(preferred, idle);

            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);
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
            var connected = await db.SetLengthAsync(SessionKey(userId)) > 0;
            if (!connected)
                return;

            var idle = await IsIdleAsync(db, userId);
            var effective = ResolveEffective(preferred, idle);
            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);

            // Friends see the masked effective value; the user's own tabs see the real
            // preferred value so the picker stays in sync (incl. invisible/dnd).
            await BroadcastStatusChangedAsync(userId, friendsStatus: effective, selfStatus: preferred, ct);
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
                await db.StringSetAsync(IdleKey(userId), "1");
            else
                await db.KeyDeleteAsync(IdleKey(userId));

            // Idle only shifts the effective status when the user is on plain "online"
            // (online ↔ away). A manual away/dnd/invisible choice is unaffected, so there's
            // nothing to recompute or broadcast.
            var preferred = await GetPreferredAsync(db, userId);
            if (preferred != PresenceStatus.Online)
                return;

            var connected = await db.SetLengthAsync(SessionKey(userId)) > 0;
            if (!connected)
                return;

            var effective = ResolveEffective(preferred, idle);
            await db.StringSetAsync(StatusKey(userId), effective, StatusTtl);

            // Auto-away is a derived state, not a masked choice — the user's own tabs
            // should reflect it too, so self and friends both get the effective value.
            await BroadcastStatusChangedAsync(userId, friendsStatus: effective, selfStatus: effective, ct);
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

    // -------------------------------------------------------------------------
    // Broadcast fan-out
    // -------------------------------------------------------------------------

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
    /// Sends StatusChanged to the user's friends (with <paramref name="friendsStatus"/>)
    /// and to the user's own connections (with <paramref name="selfStatus"/>) — the latter
    /// keeps multi-tab pickers in sync and is the only path with recipients today.
    /// </summary>
    private async Task BroadcastStatusChangedAsync(
        long userId,
        string friendsStatus,
        string selfStatus,
        CancellationToken ct
    )
    {
        await BroadcastToFriendsAsync(
            userId,
            new StatusChangedPayload(userId, friendsStatus, StatusMessage: null),
            (recipientId, payload) =>
                _broadcaster.BroadcastStatusChangedAsync(recipientId, payload, ct)
        );

        try
        {
            await _broadcaster.BroadcastStatusChangedAsync(
                userId,
                new StatusChangedPayload(userId, selfStatus, StatusMessage: null),
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
    /// Friend-recipient seam. No IFriendRepository exists yet — friends-system is
    /// Phase 4. Returns no recipients today; this is the one lookup that feature
    /// needs to fill in to make friend-facing presence broadcasts actually deliver.
    /// </summary>
    private static Task<List<long>> ResolveFriendIdsAsync(long userId) =>
        Task.FromResult(new List<long>());

    // -------------------------------------------------------------------------
    // Key helpers
    // -------------------------------------------------------------------------

    public static string StatusKey(long userId) => $"user:{userId}:status";

    public static string SessionKey(long userId) => $"session:{userId}";

    public static string PreferredKey(long userId) => $"user:{userId}:preferred";

    public static string IdleKey(long userId) => $"user:{userId}:idle";
}
