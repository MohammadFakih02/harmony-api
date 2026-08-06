namespace Harmony.Application.Interfaces.Services;

/// <summary>A user's message-render display fields — the username + avatar key the message
/// broadcast stamps on every message.</summary>
public readonly record struct UserDisplay(string Username, string? AvatarKey);

/// <summary>
/// Small read-through cache for the per-message sender display fields
/// (<see cref="UserDisplay"/>). The ScyllaMessageConsumer resolves these on the hot path for
/// every message purely to render a display name; without a cache that is a Postgres round-trip
/// (and a rented DbContext) per message.
///
/// Backed by Redis so it is shared across API/consumer instances. FAIL-OPEN: when Redis is
/// unavailable <see cref="GetAsync"/> returns null and the caller falls back to the source of
/// truth (the user repository).
///
/// Invalidated on the only two events that change these fields — a username change and an avatar
/// change — both of which already fan out a ProfileUpdated broadcast, so a briefly-stale cached
/// value also self-corrects on the client. A short TTL backstops any missed invalidation.
///
/// Key format: <c>userdisplay:{userId}</c>
/// </summary>
public interface IUserDisplayCache
{
    /// <summary>The cached display fields for a user, or null on a miss / when Redis is down.</summary>
    Task<UserDisplay?> GetAsync(long userId, CancellationToken ct = default);

    /// <summary>Populates the cache for a user (best-effort; no-op when Redis is down).</summary>
    Task SetAsync(long userId, UserDisplay value, CancellationToken ct = default);

    /// <summary>Evicts a user's cached display fields (best-effort; no-op when Redis is down).</summary>
    Task InvalidateAsync(long userId, CancellationToken ct = default);
}
