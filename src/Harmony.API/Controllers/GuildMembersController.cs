using System.Security.Claims;
using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Member moderation for a guild: kick, ban/unban, and timeout. Guild-scoped so the route-driven
/// <see cref="RequirePermissionAttribute"/> enforces the required bit (KickMembers / BanMembers /
/// TimeoutMembers). The hierarchy guard (no self/owner) and all side effects live in
/// <see cref="IGuildMemberService"/>.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/members")]
[Authorize]
[EnableRateLimiting("api")]
public class GuildMembersController : ControllerBase
{
    private readonly IGuildMemberService _members;

    public GuildMembersController(IGuildMemberService members)
    {
        _members = members;
    }

    // DELETE /api/guilds/{guildId}/members/{userId} — kick
    [HttpDelete("{userId:long}")]
    [RequirePermission(Permission.KickMembers)]
    public async Task<IActionResult> Kick(long guildId, long userId)
    {
        await _members.KickAsync(guildId, GetUserId(), userId);
        return NoContent();
    }

    // PUT /api/guilds/{guildId}/members/{userId}/timeout
    [HttpPut("{userId:long}/timeout")]
    [RequirePermission(Permission.TimeoutMembers)]
    public async Task<IActionResult> Timeout(
        long guildId,
        long userId,
        [FromBody] TimeoutMemberRequest request
    )
    {
        await _members.TimeoutAsync(guildId, GetUserId(), userId, request.DurationSeconds);
        return NoContent();
    }

    // DELETE /api/guilds/{guildId}/members/{userId}/timeout — clear timeout
    [HttpDelete("{userId:long}/timeout")]
    [RequirePermission(Permission.TimeoutMembers)]
    public async Task<IActionResult> ClearTimeout(long guildId, long userId)
    {
        await _members.ClearTimeoutAsync(guildId, GetUserId(), userId);
        return NoContent();
    }

    // GET /api/guilds/{guildId}/members/bans
    [HttpGet("bans")]
    [RequirePermission(Permission.BanMembers)]
    public async Task<IActionResult> GetBans(long guildId) =>
        Ok(await _members.GetBansAsync(guildId));

    // PUT /api/guilds/{guildId}/members/bans/{userId} — ban
    [HttpPut("bans/{userId:long}")]
    [RequirePermission(Permission.BanMembers)]
    public async Task<IActionResult> Ban(
        long guildId,
        long userId,
        [FromBody] BanMemberRequest? request
    )
    {
        await _members.BanAsync(guildId, GetUserId(), userId, request?.Reason);
        return NoContent();
    }

    // DELETE /api/guilds/{guildId}/members/bans/{userId} — unban
    [HttpDelete("bans/{userId:long}")]
    [RequirePermission(Permission.BanMembers)]
    public async Task<IActionResult> Unban(long guildId, long userId)
    {
        await _members.UnbanAsync(guildId, GetUserId(), userId);
        return NoContent();
    }

    // PATCH /api/guilds/{guildId}/members/me/nickname — set your OWN server nickname (any member).
    // ("me" can't match the {userId:long} routes, so this stays unambiguous.)
    [HttpPatch("me/nickname")]
    public async Task<IActionResult> SetOwnNickname(
        long guildId,
        [FromBody] SetNicknameRequest request
    )
    {
        await _members.SetOwnNicknameAsync(guildId, GetUserId(), request.Nickname);
        return NoContent();
    }

    // PATCH /api/guilds/{guildId}/members/{userId}/nickname — rename another member (ManageNicknames).
    [HttpPatch("{userId:long}/nickname")]
    [RequirePermission(Permission.ManageNicknames)]
    public async Task<IActionResult> SetNickname(
        long guildId,
        long userId,
        [FromBody] SetNicknameRequest request
    )
    {
        await _members.SetNicknameAsync(guildId, GetUserId(), userId, request.Nickname);
        return NoContent();
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
