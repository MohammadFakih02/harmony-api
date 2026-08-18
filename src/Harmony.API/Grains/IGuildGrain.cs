using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// Per-guild grain (key = guildId) — the D4 owner of a guild's authorization state: its roles (with
/// bits), membership + owner set, per-member role assignments, and lazily-loaded per-channel
/// permission overrides. It hydrates this snapshot from Postgres once per activation and resolves
/// effective permission bits <em>in memory</em> thereafter, replacing the Redis <c>perms:{u}:{g}</c>
/// cache (retired outside Test, like presence in D1).
///
/// <para>Single activation, single-threaded → the snapshot never races a concurrent mutation, and the
/// bulk <see cref="FilterByPermission"/> (the unread fan-out's hot path) becomes a pure in-memory
/// filter with zero I/O. On any change the write services call <see cref="InvalidateUser"/> /
/// <see cref="InvalidateGuild"/> through the unchanged <c>IPermissionService</c> seam, which drops the
/// snapshot so the next resolve re-hydrates.</para>
///
/// <para>The math itself is the shared <c>PermissionResolver</c> (also used by the Test-environment
/// <c>PermissionService</c>), so grain and service resolve identically.</para>
/// </summary>
public interface IGuildGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Effective permission bitmask for <paramref name="userId"/> in this guild. Pass
    /// <paramref name="channelId"/> to apply that channel's overrides; null for guild-level. Returns 0
    /// for a non-member; owner/Administrator resolve to all permissions.
    /// </summary>
    Task<long> ResolveBits(long userId, long? channelId);

    /// <summary>
    /// Of <paramref name="userIds"/> (in order), those whose resolved bits grant
    /// <paramref name="permissionBit"/>. In-memory over the snapshot — the batched replacement for the
    /// per-user resolve the unread fan-out used to do one Redis round-trip at a time.
    /// </summary>
    Task<List<long>> FilterByPermission(List<long> userIds, long permissionBit, long? channelId);

    /// <summary>Drops the cached snapshot after one member's roles/membership changed (assign/unassign,
    /// kick/ban, member-targeted override). The next resolve re-hydrates.</summary>
    Task InvalidateUser(long userId);

    /// <summary>Drops the cached snapshot after a guild-wide change (a role's bits, a role delete, an
    /// @everyone/role-targeted override). The next resolve re-hydrates.</summary>
    Task InvalidateGuild();
}
