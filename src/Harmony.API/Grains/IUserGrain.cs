using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// Per-user presence actor (Track D1) — keyed by userId. Holds the user's live SignalR
/// connection set and their preferred / idle / custom-status state <em>in grain memory</em>,
/// and owns the online/offline/StatusChanged fan-out that <see cref="Harmony.Infrastructure.Redis.RedisPresenceService"/>
/// previously drove through Redis. Single-writer (non-reentrant) → the connection-count
/// transitions that gate the broadcasts ("first connection → online", "last → offline") are
/// naturally race-free, without the ghost-id liveness ZSET the Redis path needed.
///
/// <para>State is intentionally ephemeral: a silo restart deactivates every grain, which is
/// correct — a restart drops every SignalR connection, so presence <em>should</em> reset. The
/// durable half (preferred status + custom message) is reloaded from Postgres on first use.</para>
/// </summary>
public interface IUserGrain : IGrainWithIntegerKey
{
    /// <summary>Registers a connection. On the first live connection, broadcasts online (unless invisible).</summary>
    Task SetOnline(string connectionId);

    /// <summary>Removes a connection. On the last one, broadcasts offline.</summary>
    Task SetOffline(string connectionId);

    /// <summary>Refreshes a connection's last-seen timestamp (the per-connection dead-man's switch). Never broadcasts.</summary>
    Task Heartbeat(string connectionId);

    /// <summary>The public effective status (online/away/dnd/offline); an invisible or disconnected user reads offline.</summary>
    Task<string> GetStatus();

    /// <summary>Caches the durable preferred status (Postgres is the source of truth; the caller persists it) and, if connected, broadcasts StatusChanged.</summary>
    Task SetPreferredStatus(string preferred);

    /// <summary>The cached preferred status (loaded from Postgres on first use). Defaults to "online".</summary>
    Task<string> GetPreferredStatus();

    /// <summary>Records the client idle flag; shifts online↔away only while preferred is "online".</summary>
    Task SetIdle(bool idle);

    /// <summary>Caches the custom status message (the caller persists it) and, if connected, broadcasts StatusChanged.</summary>
    Task SetCustomStatus(string? message);

    /// <summary>The cached custom status message (loaded from Postgres on first use), or null when none.</summary>
    Task<string?> GetStatusMessage();

    /// <summary>Whether the user has any live connection — the offline-only web-push gate.</summary>
    Task<bool> IsConnected();
}
