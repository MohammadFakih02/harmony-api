using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;

namespace Harmony.Application.Authorization;

/// <summary>
/// The pure, side-effect-free heart of permission resolution — Discord's model — extracted (D4) so the
/// two callers that resolve effective bits cannot drift:
/// <list type="bullet">
///   <item><c>PermissionService</c> (the Test-environment implementation), which reads its inputs from
///   Postgres per call and memoizes the result in Redis; and</item>
///   <item>the D4 <c>GuildGrain</c>, which reads the same inputs from its in-memory guild snapshot.</item>
/// </list>
/// Behaviour is byte-identical to the logic that previously lived inline in <c>PermissionService</c>.
///
/// <para>Resolution order:</para>
/// <list type="number">
///   <item>Not a member          → 0 (no permissions).</item>
///   <item>Guild owner           → all permissions (hard bypass).</item>
///   <item>Base = @everyone bits, OR every explicitly-assigned role's bits.</item>
///   <item>Administrator bit set → all permissions (bypass; channel overrides ignored).</item>
///   <item>Channel overrides (when supplied), applied as <c>(perms &amp; ~deny) | allow</c> in precedence
///   order: @everyone → aggregated assigned-role overrides → member-specific override.</item>
/// </list>
///
/// <para>Member timeouts (CommunicationDisabledUntil) are intentionally NOT applied here — they are
/// time-sensitive and belong to the enforcement layer, exactly as in the original service.</para>
/// </summary>
public static class PermissionResolver
{
    /// <summary>Every defined permission bit OR'd together — the result for owners/administrators.</summary>
    public static readonly long AllPermissions =
        Enum.GetValues<Permission>().Aggregate(0L, (acc, p) => acc | (long)p);

    /// <summary>
    /// Resolves the effective bitmask from already-gathered inputs. Pass <paramref name="overrides"/>
    /// = <c>null</c> for guild-level resolution (no channel scope); pass the channel's overrides
    /// (possibly empty) to apply channel scope. Used by the grain, which holds every input in memory.
    /// </summary>
    public static long Resolve(
        bool isMember,
        bool isOwner,
        long everyoneBits,
        IReadOnlyList<long> memberRoleBits,
        IReadOnlyList<ChannelPermissionOverride>? overrides,
        long? everyoneRoleId,
        IReadOnlySet<long> memberRoleIds,
        long userId
    )
    {
        if (!isMember)
            return 0; // not a member → no permissions

        if (isOwner)
            return AllPermissions; // owner bypasses everything

        var perms = everyoneBits;
        foreach (var bits in memberRoleBits)
            perms |= bits;

        if ((perms & (long)Permission.Administrator) != 0)
            return AllPermissions; // Administrator bypasses overrides too

        if (overrides is null)
            return perms;

        return ApplyChannelOverrides(perms, overrides, everyoneRoleId, memberRoleIds, userId);
    }

    /// <summary>
    /// Applies channel overrides as <c>(perms &amp; ~deny) | allow</c> in Discord's precedence order:
    /// @everyone, then aggregated assigned-role overrides, then the member-specific override.
    /// </summary>
    public static long ApplyChannelOverrides(
        long perms,
        IReadOnlyList<ChannelPermissionOverride> overrides,
        long? everyoneRoleId,
        IReadOnlySet<long> memberRoleIds,
        long userId
    )
    {
        // 1. @everyone role override
        if (everyoneRoleId is { } everyoneId)
        {
            var everyoneOverride = overrides.FirstOrDefault(o =>
                o.TargetType == "role" && o.TargetId == everyoneId
            );
            if (everyoneOverride is not null)
                perms = (perms & ~everyoneOverride.DenyBits) | everyoneOverride.AllowBits;
        }

        // 2. Aggregated overrides for the member's assigned roles (deny then allow, combined)
        long rolesAllow = 0;
        long rolesDeny = 0;
        foreach (var o in overrides)
        {
            if (o.TargetType == "role" && o.TargetId != everyoneRoleId && memberRoleIds.Contains(o.TargetId))
            {
                rolesAllow |= o.AllowBits;
                rolesDeny |= o.DenyBits;
            }
        }
        perms = (perms & ~rolesDeny) | rolesAllow;

        // 3. Member-specific override (highest precedence)
        var memberOverride = overrides.FirstOrDefault(o =>
            o.TargetType == "user" && o.TargetId == userId
        );
        if (memberOverride is not null)
            perms = (perms & ~memberOverride.DenyBits) | memberOverride.AllowBits;

        return perms;
    }
}
