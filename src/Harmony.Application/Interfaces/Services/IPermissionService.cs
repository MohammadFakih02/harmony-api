using Harmony.Domain.Domain.Enums;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Resolves a member's effective permission bits for a guild (optionally scoped to a
/// channel, applying its overrides). Results are cached in Redis with a short TTL and
/// invalidated when roles or overrides change.
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Effective permission bitmask for <paramref name="userId"/> in <paramref name="guildId"/>.
    /// Pass <paramref name="channelId"/> to apply that channel's overrides; null for guild-level.
    /// Returns 0 if the user is not a member. Owner/Administrator resolve to all permissions.
    /// </summary>
    Task<long> ResolveAsync(
        long userId,
        long guildId,
        long? channelId = null,
        CancellationToken ct = default
    );

    /// <summary>Convenience check: does the resolved bitmask grant <paramref name="permission"/>?</summary>
    Task<bool> HasAsync(
        long userId,
        long guildId,
        Permission permission,
        long? channelId = null,
        CancellationToken ct = default
    );

    /// <summary>Drops the cached permissions for one member of a guild (all channels).</summary>
    Task InvalidateUserAsync(long userId, long guildId, CancellationToken ct = default);

    /// <summary>Drops the cached permissions for every member of a guild (e.g. a role's bits changed).</summary>
    Task InvalidateGuildAsync(long guildId, CancellationToken ct = default);
}
