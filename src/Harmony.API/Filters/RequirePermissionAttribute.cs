using Harmony.Domain.Domain.Enums;

namespace Harmony.API.Filters;

/// <summary>
/// Declares that an action (or controller) requires the given permission bit, enforced by
/// <see cref="PermissionAuthorizationFilter"/>. The guild is read from the <c>guildId</c>
/// (or <c>id</c>) route value and, when present, the <c>channelId</c> route value scopes the
/// check to that channel so its overrides apply. Stackable — all declared permissions must hold.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : Attribute
{
    public Permission Permission { get; }

    public RequirePermissionAttribute(Permission permission) => Permission = permission;
}
