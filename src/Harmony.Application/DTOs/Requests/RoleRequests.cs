namespace Harmony.Application.DTOs.Requests;

/// <summary>Create a role. Omitted fields take their defaults (no permissions, uncolored, not hoisted).
/// The new role is created at the bottom of the hierarchy (just above @everyone).</summary>
public record CreateRoleRequest(
    string Name,
    int? Color,
    long? PermissionBits,
    bool? IsHoisted,
    bool? IsMentionable
);

/// <summary>Partial update — only non-null fields are applied. Name is ignored for the @everyone role.</summary>
public record UpdateRoleRequest(
    string? Name,
    int? Color,
    long? PermissionBits,
    bool? IsHoisted,
    bool? IsMentionable
);

/// <summary>Bulk position update for drag-reorder. Each entry sets one role's new position.</summary>
public record ReorderRolesRequest(List<RolePosition> Positions);

public record RolePosition(long RoleId, int Position);
