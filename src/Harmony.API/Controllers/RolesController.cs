using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Guild role management. Listing is any-member (clients need roles for display/coloring); all writes
/// need <see cref="Permission.ManageRoles"/> via the route-driven <see cref="RequirePermissionAttribute"/>.
/// The hierarchy + grant rules and side effects live in <see cref="IRoleService"/>.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/roles")]
[Authorize]
[EnableRateLimiting("api")]
public class RolesController : HarmonyControllerBase
{
    private readonly IRoleService _roles;
    private readonly IGuildRepository _guilds;

    public RolesController(IRoleService roles, IGuildRepository guilds)
    {
        _roles = roles;
        _guilds = guilds;
    }

    // GET /api/guilds/{guildId}/roles — any member may read the role list.
    [HttpGet]
    public async Task<IActionResult> List(long guildId)
    {
        if (!await _guilds.IsMemberAsync(guildId, GetUserId()))
            return Forbid();
        return Ok(await _roles.GetRolesAsync(guildId));
    }

    // POST /api/guilds/{guildId}/roles
    [HttpPost]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Create(long guildId, [FromBody] CreateRoleRequest request)
    {
        var role = await _roles.CreateRoleAsync(guildId, GetUserId(), request);
        return Ok(role);
    }

    // PATCH /api/guilds/{guildId}/roles/positions — bulk reorder (before {roleId} so it isn't shadowed).
    [HttpPatch("positions")]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Reorder(long guildId, [FromBody] ReorderRolesRequest request)
    {
        await _roles.ReorderRolesAsync(guildId, GetUserId(), request);
        return NoContent();
    }

    // PATCH /api/guilds/{guildId}/roles/{roleId}
    [HttpPatch("{roleId:long}")]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Update(
        long guildId,
        long roleId,
        [FromBody] UpdateRoleRequest request
    )
    {
        var role = await _roles.UpdateRoleAsync(guildId, GetUserId(), roleId, request);
        return Ok(role);
    }

    // DELETE /api/guilds/{guildId}/roles/{roleId}
    [HttpDelete("{roleId:long}")]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Delete(long guildId, long roleId)
    {
        await _roles.DeleteRoleAsync(guildId, GetUserId(), roleId);
        return NoContent();
    }

    // PUT /api/guilds/{guildId}/roles/{roleId}/members/{userId} — assign
    [HttpPut("{roleId:long}/members/{userId:long}")]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Assign(long guildId, long roleId, long userId)
    {
        await _roles.AssignRoleAsync(guildId, GetUserId(), roleId, userId);
        return NoContent();
    }

    // DELETE /api/guilds/{guildId}/roles/{roleId}/members/{userId} — unassign
    [HttpDelete("{roleId:long}/members/{userId:long}")]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Unassign(long guildId, long roleId, long userId)
    {
        await _roles.UnassignRoleAsync(guildId, GetUserId(), roleId, userId);
        return NoContent();
    }

}
