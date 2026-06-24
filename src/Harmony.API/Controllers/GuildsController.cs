using System.Security.Claims;
using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/guilds")]
[Authorize]
[EnableRateLimiting("api")]
public class GuildsController : ControllerBase
{
    private readonly IGuildRepository _guilds;
    private readonly IRoleRepository _roles;
    private readonly ISnowflakeIdGenerator _snowflake;

    public GuildsController(
        IGuildRepository guilds,
        IRoleRepository roles,
        ISnowflakeIdGenerator snowflake
    )
    {
        _guilds = guilds;
        _roles = roles;
        _snowflake = snowflake;
    }

    // POST /api/guilds
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGuildRequest request)
    {
        var userId = GetUserId();

        var guild = new Guild
        {
            Id = _snowflake.NextId(),
            Name = request.Name,
            Description = request.Description,
            OwnerId = userId,
            IsPublic = false,
            MemberCount = 1,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await _guilds.AddAsync(guild);

        // Creator becomes a member automatically
        var member = new GuildMember
        {
            UserId = userId,
            GuildId = guild.Id,
            IsOwner = true,
            JoinedAt = guild.CreatedAt
        };

        await _guilds.AddMemberAsync(member);

        // Default @everyone role — the permission baseline applied implicitly to every
        // member (no RoleAssignment row needed). IsDefault marks it so the permission
        // resolver can find it. Additional roles are layered on top in later features.
        var everyone = new Role
        {
            Id = _snowflake.NextId(),
            GuildId = guild.Id,
            Name = "@everyone",
            Color = 0,
            PermissionBits = (long)Permission.DefaultEveryone,
            Position = 0,
            IsHoisted = false,
            IsMentionable = false,
            IsDefault = true,
            CreatedAt = guild.CreatedAt
        };

        await _roles.AddAsync(everyone);

        await _guilds.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = guild.Id }, ToResponse(guild));
    }

    // GET /api/guilds/{id}
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var guild = await _guilds.GetByIdAsync(id);
        if (guild is null) return NotFound();

        if (!await _guilds.IsMemberAsync(id, GetUserId()))
            return Forbid();

        return Ok(ToResponse(guild));
    }

    // PATCH /api/guilds/{id}
    [HttpPatch("{id:long}")]
    [RequirePermission(Permission.ManageGuild)]
    public async Task<IActionResult> Update(long id, [FromBody] UpdateGuildRequest request)
    {
        var guild = await _guilds.GetByIdAsync(id);
        if (guild is null) return NotFound();

        if (request.Name is not null) guild.Name = request.Name;
        if (request.Description is not null) guild.Description = request.Description;
        if (request.IsPublic is not null) guild.IsPublic = request.IsPublic.Value;

        await _guilds.SaveChangesAsync();

        return Ok(ToResponse(guild));
    }

    // DELETE /api/guilds/{id}
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var guild = await _guilds.GetByIdAsync(id);
        if (guild is null) return NotFound();

        if (guild.OwnerId != GetUserId())
            return Forbid();

        await _guilds.DeleteAsync(guild);
        await _guilds.SaveChangesAsync();

        return NoContent();
    }

    // Joining a guild is now done by redeeming an invite — see InvitesController
    // (POST /api/invites/{code}/join).

    // DELETE /api/guilds/{id}/leave
    [HttpDelete("{id:long}/leave")]
    public async Task<IActionResult> Leave(long id)
    {
        var userId = GetUserId();
        var member = await _guilds.GetMemberAsync(id, userId);
        if (member is null) return NotFound();

        if (member.IsOwner)
            return BadRequest(new { error = "Owner cannot leave. Transfer ownership or delete the guild." });

        await _guilds.RemoveMemberAsync(member);

        var guild = await _guilds.GetByIdAsync(id);
        if (guild is not null) guild.MemberCount--;

        await _guilds.SaveChangesAsync();

        return NoContent();
    }

    // GET /api/guilds/{id}/members
    [HttpGet("{id:long}/members")]
    public async Task<IActionResult> GetMembers(long id)
    {
        if (!await _guilds.IsMemberAsync(id, GetUserId()))
            return Forbid();

        var members = await _guilds.GetMembersAsync(id);
        return Ok(members.Select(ToMemberResponse));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private long GetUserId() =>
        long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static GuildResponse ToResponse(Guild g) =>
        new(g.Id, g.Name, g.Description, g.OwnerId, g.IconKey, g.BannerKey,
            g.IsPublic, g.MemberCount, g.CreatedAt);

    private static GuildMemberResponse ToMemberResponse(GuildMember m) =>
        new(m.UserId, m.User.UserName!, m.Nickname,
            m.User.AvatarKey, m.IsOwner, m.JoinedAt);
}