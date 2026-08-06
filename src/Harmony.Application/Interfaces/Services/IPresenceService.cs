namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Tracks per-user online/offline presence across multiple SignalR connections
/// (tabs/devices) and broadcasts OnlineStatus/OfflineStatus to friends.
///
/// All Redis interaction fails open: if Redis is unavailable, presence is simply
/// not tracked/read, never throwing — a missing presence indicator is far
/// preferable to a failed connection lifecycle. Per-recipient broadcast failures
/// are swallowed individually so one dead connection never aborts the fan-out.
/// </summary>
public interface IPresenceService
{
    /// <summary>
    /// Registers a new connection for the user. If this is the user's first
    /// active connection, marks them online and broadcasts OnlineStatus to
    /// their friends. Additional connections (multi-tab) are tracked silently.
    /// </summary>
    Task SetOnlineAsync(long userId, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Removes a connection for the user. If this was their last active
    /// connection, marks them offline and broadcasts OfflineStatus to their
    /// friends. If other connections remain, this is a silent no-op.
    /// </summary>
    Task SetOfflineAsync(long userId, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the user's online TTL and last-heartbeat timestamp — including the
    /// per-connection liveness score, so ghost connection ids (API restarts, dead sockets)
    /// age out and can't hold a user "online". Called periodically by each connected
    /// client. Never broadcasts.
    /// </summary>
    Task HeartbeatAsync(long userId, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Reads a user's current public effective status (online/away/dnd/offline;
    /// an invisible user reads as offline here). Returns "offline" if absent or
    /// Redis is unavailable.
    /// </summary>
    Task<string> GetStatusAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Reads several users' public effective statuses in one round-trip (MGET).
    /// Absent/offline users map to "offline". Used by the member-list dots.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetStatusesAsync(
        IEnumerable<long> userIds,
        CancellationToken ct = default
    );

    /// <summary>
    /// Reads several users' custom status messages in one round-trip. Returns null for a
    /// user with no message (or whose cache is cold). The caller hides the message of a
    /// user who appears offline.
    /// </summary>
    Task<IReadOnlyDictionary<long, string?>> GetStatusMessagesAsync(
        IEnumerable<long> userIds,
        CancellationToken ct = default
    );

    /// <summary>
    /// Caches the user's new custom status <paramref name="message"/> (null = none) and,
    /// if they're connected, broadcasts StatusChanged carrying it — to friends (masked to
    /// no message when they appear offline) and to the user's own tabs. The durable copy
    /// lives in Postgres (the caller persists it); this is the live-presence half.
    /// </summary>
    Task SetCustomStatusAsync(long userId, string? message, CancellationToken ct = default);

    /// <summary>
    /// Sets the user's durable preferred status (online/away/dnd/invisible). The
    /// caller persists it to Postgres; this updates the Redis cache, recomputes
    /// the effective status if the user is connected, and broadcasts StatusChanged
    /// (the public effective value to friends, the raw preferred value to the
    /// user's own connections so their other tabs sync).
    /// </summary>
    Task SetPreferredStatusAsync(long userId, string preferred, CancellationToken ct = default);

    /// <summary>
    /// Reads the user's preferred status from the Redis cache, falling back to
    /// Postgres on a cache miss. Returns "online" if unknown.
    /// </summary>
    Task<string> GetPreferredStatusAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Records the client-reported idle flag (the 15-min inactivity signal). Only
    /// affects the effective status when the preferred status is "online"
    /// (online ↔ away); a manual away/dnd/invisible choice is unchanged. Broadcasts
    /// StatusChanged when the effective status actually changes.
    /// </summary>
    Task SetIdleAsync(long userId, bool idle, CancellationToken ct = default);

    /// <summary>
    /// Crash-recovery sweep: marks offline every user whose last heartbeat in the
    /// <c>presence:online</c> ZSET is older than <paramref name="staleThreshold"/> — the
    /// ghost left when a client crashes or the server restarts before
    /// <see cref="SetOfflineAsync"/> could run, so the graceful disconnect path never
    /// fired. For each such user it clears the lingering session set / status / idle keys,
    /// removes the ZSET entry, and broadcasts OfflineStatus to friends and co-guild members.
    /// Returns the number of users reaped. Fails open (Redis unavailable → 0, never throws).
    /// </summary>
    Task<int> SweepStaleAsync(TimeSpan staleThreshold, CancellationToken ct = default);

    /// <summary>
    /// Whether the user has any active SignalR connection (non-empty session set) —
    /// the gate for offline-only web push. Fails CLOSED for push: Redis unavailable
    /// returns true ("assume connected"), so an uncertain state skips the push rather
    /// than risk buzzing a user who is looking at the app.
    /// </summary>
    Task<bool> IsConnectedAsync(long userId, CancellationToken ct = default);
}
