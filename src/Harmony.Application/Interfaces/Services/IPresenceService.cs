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
    /// Refreshes the user's online TTL and last-heartbeat timestamp. Called
    /// periodically by the client while connected. Never broadcasts.
    /// </summary>
    Task HeartbeatAsync(long userId, CancellationToken ct = default);

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
}
