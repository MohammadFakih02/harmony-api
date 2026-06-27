using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Role management for a guild: CRUD, reorder, and member assignment. The ManageRoles bit is gated at
/// the endpoint ([RequirePermission]); this service owns the two safety rules and the side effects:
///   • Hierarchy — you can only act on roles <b>below your own highest role</b> (the owner bypasses;
///     a non-owner Administrator does <i>not</i> bypass hierarchy).
///   • Grant rule — you can only grant/revoke permission bits you yourself hold (owner/Administrator
///     hold all bits, so this is a no-op for them).
/// Plus audit-log writes, permission-cache invalidation, and SignalR broadcasts. Outcomes use the
/// standard exceptions (KeyNotFound → 404, UnauthorizedAccess → 403, Argument/InvalidOperation → 400).
/// </summary>
public interface IRoleService
{
    Task<IReadOnlyList<RoleResponse>> GetRolesAsync(long guildId, CancellationToken ct = default);

    Task<RoleResponse> CreateRoleAsync(
        long guildId,
        long actorId,
        CreateRoleRequest request,
        CancellationToken ct = default
    );

    Task<RoleResponse> UpdateRoleAsync(
        long guildId,
        long actorId,
        long roleId,
        UpdateRoleRequest request,
        CancellationToken ct = default
    );

    Task DeleteRoleAsync(long guildId, long actorId, long roleId, CancellationToken ct = default);

    Task ReorderRolesAsync(
        long guildId,
        long actorId,
        ReorderRolesRequest request,
        CancellationToken ct = default
    );

    Task AssignRoleAsync(
        long guildId,
        long actorId,
        long roleId,
        long userId,
        CancellationToken ct = default
    );

    Task UnassignRoleAsync(
        long guildId,
        long actorId,
        long roleId,
        long userId,
        CancellationToken ct = default
    );
}
