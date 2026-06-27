using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Implements role management. The ManageRoles bit is enforced at the endpoint; this service owns the
/// hierarchy + grant rules (see <see cref="IRoleService"/>), audit writes, permission-cache
/// invalidation, and broadcasts. State commits before any side effect, and broadcasts are best-effort.
/// </summary>
public class RoleService : IRoleService
{
    private readonly IGuildRepository _guilds;
    private readonly IRoleRepository _roles;
    private readonly IPermissionService _permissions;
    private readonly IAuditLogService _audit;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly ILogger<RoleService> _logger;

    public RoleService(
        IGuildRepository guilds,
        IRoleRepository roles,
        IPermissionService permissions,
        IAuditLogService audit,
        IHubBroadcaster broadcaster,
        ISnowflakeIdGenerator snowflake,
        ILogger<RoleService> logger
    )
    {
        _guilds = guilds;
        _roles = roles;
        _permissions = permissions;
        _audit = audit;
        _broadcaster = broadcaster;
        _snowflake = snowflake;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RoleResponse>> GetRolesAsync(
        long guildId,
        CancellationToken ct = default
    ) => (await _roles.GetByGuildAsync(guildId)).Select(ToResponse).ToList();

    public async Task<RoleResponse> CreateRoleAsync(
        long guildId,
        long actorId,
        CreateRoleRequest request,
        CancellationToken ct = default
    )
    {
        var ctx = await GetActorContextAsync(guildId, actorId, ct);

        var bits = request.PermissionBits ?? 0;
        EnsureCanChangeBits(ctx, 0, bits);

        var role = new Role
        {
            Id = _snowflake.NextId(),
            GuildId = guildId,
            Name = request.Name,
            Color = request.Color ?? 0,
            PermissionBits = bits,
            // Created at the bottom of the hierarchy (just above @everyone=0). Avoids minting a role
            // above the creator that they then couldn't manage; reorder sets explicit ranks later.
            Position = 1,
            IsHoisted = request.IsHoisted ?? false,
            IsMentionable = request.IsMentionable ?? false,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _roles.AddAsync(role);
        await _roles.SaveChangesAsync();

        // New role grants no one anything until assigned, so no cache invalidation is needed here.
        await _audit.LogAsync(
            guildId, actorId, AuditLogAction.RoleCreate,
            targetId: role.Id, changes: new { role.Name, permissionBits = bits }, ct: ct
        );
        var response = ToResponse(role);
        await SafeBroadcast(() => _broadcaster.BroadcastRoleCreatedAsync(guildId, response, ct), guildId);
        return response;
    }

    public async Task<RoleResponse> UpdateRoleAsync(
        long guildId,
        long actorId,
        long roleId,
        UpdateRoleRequest request,
        CancellationToken ct = default
    )
    {
        var ctx = await GetActorContextAsync(guildId, actorId, ct);
        var role = await GetGuildRoleOrThrowAsync(guildId, roleId);
        EnsureCanManage(ctx, role);

        var newBits = request.PermissionBits ?? role.PermissionBits;
        EnsureCanChangeBits(ctx, role.PermissionBits, newBits);

        var bitsChanged = newBits != role.PermissionBits;

        // The @everyone role keeps its name; everything else is editable.
        if (request.Name is not null && !role.IsDefault) role.Name = request.Name;
        if (request.Color is { } color) role.Color = color;
        if (request.IsHoisted is { } hoisted) role.IsHoisted = hoisted;
        if (request.IsMentionable is { } mentionable) role.IsMentionable = mentionable;
        role.PermissionBits = newBits;

        await _roles.SaveChangesAsync();

        // A bits change alters every member's resolved permissions — drop the guild's cached perms.
        if (bitsChanged) await _permissions.InvalidateGuildAsync(guildId, ct);

        await _audit.LogAsync(
            guildId, actorId, AuditLogAction.RoleUpdate,
            targetId: role.Id, changes: new { role.Name, permissionBits = role.PermissionBits }, ct: ct
        );
        var response = ToResponse(role);
        await SafeBroadcast(() => _broadcaster.BroadcastRoleUpdatedAsync(guildId, response, ct), guildId);
        return response;
    }

    public async Task DeleteRoleAsync(
        long guildId,
        long actorId,
        long roleId,
        CancellationToken ct = default
    )
    {
        var ctx = await GetActorContextAsync(guildId, actorId, ct);
        var role = await GetGuildRoleOrThrowAsync(guildId, roleId);

        if (role.IsDefault)
            throw new InvalidOperationException("The @everyone role cannot be deleted.");
        EnsureCanManage(ctx, role);

        _roles.Remove(role); // RoleAssignments cascade-delete via the FK
        await _roles.SaveChangesAsync();

        // Members lose the role's bits → invalidate the whole guild's cached perms.
        await _permissions.InvalidateGuildAsync(guildId, ct);
        await _audit.LogAsync(
            guildId, actorId, AuditLogAction.RoleDelete, targetId: roleId, changes: new { role.Name }, ct: ct
        );
        await SafeBroadcast(
            () => _broadcaster.BroadcastRoleDeletedAsync(guildId, new RoleDeletedPayload(guildId, roleId), ct),
            guildId
        );
    }

    public async Task ReorderRolesAsync(
        long guildId,
        long actorId,
        ReorderRolesRequest request,
        CancellationToken ct = default
    )
    {
        var ctx = await GetActorContextAsync(guildId, actorId, ct);
        var byId = (await _roles.GetByGuildAsync(guildId)).ToDictionary(r => r.Id);

        // Validate every entry first so the reorder is all-or-nothing.
        foreach (var entry in request.Positions)
        {
            if (!byId.TryGetValue(entry.RoleId, out var role))
                throw new KeyNotFoundException("Role not found in this guild.");
            if (role.IsDefault)
                throw new InvalidOperationException("The @everyone role cannot be moved.");
            EnsureCanManage(ctx, role); // can't move a role at/above your own rank
            if (entry.Position < 1)
                throw new ArgumentException("Role position must be 1 or greater.");
        }

        foreach (var entry in request.Positions)
            byId[entry.RoleId].Position = entry.Position;

        await _roles.SaveChangesAsync();

        // Position doesn't affect resolved permission bits, so no cache invalidation is needed.
        foreach (var entry in request.Positions)
            await SafeBroadcast(
                () => _broadcaster.BroadcastRoleUpdatedAsync(guildId, ToResponse(byId[entry.RoleId]), ct),
                guildId
            );
    }

    public async Task AssignRoleAsync(
        long guildId,
        long actorId,
        long roleId,
        long userId,
        CancellationToken ct = default
    )
    {
        var ctx = await GetActorContextAsync(guildId, actorId, ct);
        var role = await GetGuildRoleOrThrowAsync(guildId, roleId);

        if (role.IsDefault)
            throw new InvalidOperationException("The @everyone role is assigned to all members implicitly.");
        EnsureCanManage(ctx, role); // can't hand out a role at/above your own rank

        if (await _guilds.GetMemberAsync(guildId, userId) is null)
            throw new KeyNotFoundException("Member not found in this guild.");

        if (await _roles.GetAssignmentAsync(roleId, userId) is null)
        {
            await _roles.AddAssignmentAsync(new RoleAssignment
            {
                UserId = userId,
                RoleId = roleId,
                GuildId = guildId,
                AssignedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            await _roles.SaveChangesAsync();
            await _permissions.InvalidateUserAsync(userId, guildId, ct);
            await AuditAndBroadcastMemberRolesAsync(guildId, actorId, userId, ct);
        }
    }

    public async Task UnassignRoleAsync(
        long guildId,
        long actorId,
        long roleId,
        long userId,
        CancellationToken ct = default
    )
    {
        var ctx = await GetActorContextAsync(guildId, actorId, ct);
        var role = await GetGuildRoleOrThrowAsync(guildId, roleId);

        if (role.IsDefault)
            throw new InvalidOperationException("The @everyone role cannot be removed from a member.");
        EnsureCanManage(ctx, role);

        if (await _roles.GetAssignmentAsync(roleId, userId) is { } assignment)
        {
            _roles.RemoveAssignment(assignment);
            await _roles.SaveChangesAsync();
            await _permissions.InvalidateUserAsync(userId, guildId, ct);
            await AuditAndBroadcastMemberRolesAsync(guildId, actorId, userId, ct);
        }
    }

    // -------------------------------------------------------------------------
    // Rules
    // -------------------------------------------------------------------------

    private sealed record ActorContext(bool IsOwner, long ResolvedBits, int HighestPosition);

    private async Task<ActorContext> GetActorContextAsync(long guildId, long actorId, CancellationToken ct)
    {
        var guild = await _guilds.GetByIdAsync(guildId) ?? throw new KeyNotFoundException("Guild not found.");
        var isOwner = guild.OwnerId == actorId;
        var resolved = await _permissions.ResolveAsync(actorId, guildId, null, ct);

        // Owner sits above everything; otherwise the highest assigned role's position (@everyone=0
        // is the floor). A non-owner Administrator does NOT bypass hierarchy.
        var highest = 0;
        if (!isOwner)
        {
            var memberRoles = await _roles.GetMemberRolesAsync(guildId, actorId);
            if (memberRoles.Count > 0) highest = memberRoles.Max(r => r.Position);
        }

        return new ActorContext(isOwner, resolved, isOwner ? int.MaxValue : highest);
    }

    /// <summary>Hierarchy: you can only act on a role strictly below your highest (owner bypasses).</summary>
    private static void EnsureCanManage(ActorContext ctx, Role role)
    {
        if (!ctx.IsOwner && ctx.HighestPosition <= role.Position)
            throw new UnauthorizedAccessException(
                "You can only manage roles below your highest role."
            );
    }

    /// <summary>Grant rule: every permission bit you add or remove must be one you hold yourself.</summary>
    private static void EnsureCanChangeBits(ActorContext ctx, long oldBits, long newBits)
    {
        var changed = oldBits ^ newBits;
        if ((changed & ~ctx.ResolvedBits) != 0)
            throw new UnauthorizedAccessException(
                "You can only grant or revoke permissions that you have yourself."
            );
    }

    private async Task<Role> GetGuildRoleOrThrowAsync(long guildId, long roleId)
    {
        var role = await _roles.GetByIdAsync(roleId);
        if (role is null || role.GuildId != guildId)
            throw new KeyNotFoundException("Role not found in this guild.");
        return role;
    }

    private async Task AuditAndBroadcastMemberRolesAsync(
        long guildId,
        long actorId,
        long userId,
        CancellationToken ct
    )
    {
        var roleIds = await _roles.GetMemberRoleIdsAsync(guildId, userId);
        await _audit.LogAsync(
            guildId, actorId, AuditLogAction.MemberRoleUpdate, targetId: userId, changes: new { roleIds }, ct: ct
        );
        await SafeBroadcast(
            () => _broadcaster.BroadcastMemberRoleUpdatedAsync(
                guildId, new MemberRoleUpdatedPayload(guildId, userId, roleIds), ct
            ),
            guildId
        );
    }

    private async Task SafeBroadcast(Func<Task> broadcast, long guildId)
    {
        try
        {
            await broadcast();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast a role event for guild {GuildId}", guildId);
        }
    }

    private static RoleResponse ToResponse(Role r) =>
        new(r.Id, r.GuildId, r.Name, r.Color, r.PermissionBits, r.Position, r.IsHoisted, r.IsMentionable, r.IsDefault);
}
