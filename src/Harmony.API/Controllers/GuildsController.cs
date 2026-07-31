using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces; // For IMessagePublisher
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/guilds")]
[Authorize]
[EnableRateLimiting("api")]
public class GuildsController : HarmonyControllerBase
{
    private readonly IGuildRepository _guilds;
    private readonly IRoleRepository _roles;
    private readonly IChannelRepository _channels;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IPermissionService _permissions;
    private readonly IMessagePublisher _publisher;

    public GuildsController(
        IGuildRepository guilds,
        IRoleRepository roles,
        IChannelRepository channels,
        ISnowflakeIdGenerator snowflake,
        IPermissionService permissions,
        IMessagePublisher publisher
    )
    {
        _guilds = guilds;
        _roles = roles;
        _channels = channels;
        _snowflake = snowflake;
        _permissions = permissions;
        _publisher = publisher;
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

        // Default #general text channel — a fresh guild needs somewhere to talk, and the
        // member-join welcome flow needs a target channel. Also pinned as the welcome channel.
        var general = new Channel
        {
            Id = _snowflake.NextId(),
            GuildId = guild.Id,
            Name = "general",
            Type = "text",
            Position = 0,
            CreatedAt = guild.CreatedAt
        };

        await _channels.AddAsync(general);
        guild.WelcomeChannelId = general.Id;

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
        if (request.RequireVerifiedEmail is not null) guild.RequireVerifiedEmail = request.RequireVerifiedEmail.Value;

        await _guilds.SaveChangesAsync();

        return Ok(ToResponse(guild));
    }

    // PATCH /api/guilds/{id}/welcome — configure the welcome channel + greeting + system-message toggle.
    [HttpPatch("{id:long}/welcome")]
    [RequirePermission(Permission.ManageGuild)]
    public async Task<IActionResult> UpdateWelcome(long id, [FromBody] UpdateGuildWelcomeRequest request)
    {
        var guild = await _guilds.GetByIdAsync(id);
        if (guild is null) return NotFound();

        // A welcome channel (when set) must be a text channel of THIS guild — never trust the id.
        if (request.WelcomeChannelId is { } channelId)
        {
            var channel = await _channels.GetByIdAndGuildIdAsync(channelId, id);
            if (channel is null || channel.Type != "text")
                return BadRequest(new { error = "Welcome channel must be a text channel in this guild." });
        }

        guild.WelcomeChannelId = request.WelcomeChannelId; // null clears → falls back to default channel
        // Blank message normalizes to null (use the built-in default notice).
        guild.WelcomeMessage = string.IsNullOrWhiteSpace(request.WelcomeMessage) ? null : request.WelcomeMessage;
        guild.SystemMessagesEnabled = request.SystemMessagesEnabled;

        await _guilds.SaveChangesAsync();

        return Ok(ToResponse(guild));
    }

    // DELETE /api/guilds/{id}
    // Soft delete (§5.71 #5): the guild drops off every member's rail and 404s everywhere, but the
    // owner can restore it from their Trash until the 30-day auto-purge (or a permanent delete). Its
    // channels/messages are left intact — restore brings the whole server back.
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var guild = await _guilds.GetByIdAsync(id);
        if (guild is null) return NotFound();

        if (guild.OwnerId != GetUserId())
            return Forbid();

        guild.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _guilds.SaveChangesAsync();

        return NoContent();
    }

    // GET /api/guilds/trash — guilds the caller owns that they've soft-deleted (their global Trash).
    [HttpGet("trash")]
    public async Task<IActionResult> Trash()
    {
        var deleted = await _guilds.GetDeletedByOwnerAsync(GetUserId());
        return Ok(
            deleted.Select(g => new DeletedGuildResponse(g.Id, g.Name, g.IconKey, g.DeletedAt))
        );
    }

    // POST /api/guilds/{id}/restore — owner-only; clears the tombstone so the guild (and everything
    // under it) comes back for every member on their next load.
    [HttpPost("{id:long}/restore")]
    public async Task<IActionResult> Restore(long id)
    {
        var guild = await _guilds.GetByIdIncludingDeletedAsync(id);
        if (guild is null || guild.DeletedAt is null)
            return NotFound();
        if (guild.OwnerId != GetUserId())
            return Forbid();

        guild.DeletedAt = null;
        await _guilds.SaveChangesAsync();
        return Ok(ToResponse(guild));
    }

    // DELETE /api/guilds/{id}/permanent — owner-only; hard delete a trashed guild NOW instead of
    // waiting for the sweep. Purges each text channel's Scylla partition + search index (the EF
    // cascade only covers Postgres), then removes the guild (cascading its channels/members/roles).
    [HttpDelete("{id:long}/permanent")]
    public async Task<IActionResult> PermanentDelete(long id)
    {
        var guild = await _guilds.GetByIdIncludingDeletedAsync(id);
        // Only an already-trashed guild can be permanently deleted — a live one must be soft-deleted
        // first, so there's always a recoverable window.
        if (guild is null || guild.DeletedAt is null)
            return NotFound();
        if (guild.OwnerId != GetUserId())
            return Forbid();

        var channelIds = await _channels.GetTextChannelIdsByGuildIncludingDeletedAsync(id);

        await _guilds.DeleteAsync(guild);
        await _guilds.SaveChangesAsync();

        // Fan out the per-channel purge after the Postgres cascade lands. Best-effort per channel —
        // the guild is already gone; a failed publish just leaves an orphaned Scylla partition the
        // sweep never revisits, which is harmless (unreachable data), not a correctness break.
        foreach (var channelId in channelIds)
            await _publisher.PublishChannelDeletedAsync(
                new ChannelDeletedEvent(channelId, id, DateTimeOffset.UtcNow)
            );

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
        await _guilds.SaveChangesAsync();

        // Atomic, zero-clamped — and no longer needs to load the whole guild (with its channels and
        // roles) just to decrement one column.
        await _guilds.AdjustMemberCountAsync(id, -1);

        return NoContent();
    }

    // GET /api/guilds/{id}/members
    [HttpGet("{id:long}/members")]
    public async Task<IActionResult> GetMembers(long id)
    {
        if (!await _guilds.IsMemberAsync(id, GetUserId()))
            return Forbid();

        var members = await _guilds.GetMembersAsync(id);
        var rolesByMember = await _roles.GetRoleIdsByMemberAsync(id);
        return Ok(members.Select(m => ToMemberResponse(m, rolesByMember.GetValueOrDefault(m.UserId) ?? [])));
    }

    // GET /api/guilds/{id}/permissions
    // The caller's guild-level capabilities (resolved bits → booleans) so the client can show/hide
    // moderation + management UI without ever reasoning about permission bits. Guild-scoped (no
    // channel overrides applied); the per-channel endpoint stays on ChannelsController.
    [HttpGet("{id:long}/permissions")]
    public async Task<IActionResult> GetMyGuildCapabilities(long id)
    {
        var bits = await _permissions.ResolveAsync(GetUserId(), id);
        bool Has(Permission p) => (bits & (long)p) == (long)p;

        return Ok(new GuildCapabilitiesResponse(
            CanManageGuild: Has(Permission.ManageGuild),
            CanManageChannels: Has(Permission.ManageChannels),
            CanManageRoles: Has(Permission.ManageRoles),
            CanCreateInvite: Has(Permission.CreateInvite),
            CanManageInvites: Has(Permission.ManageInvites),
            CanKick: Has(Permission.KickMembers),
            CanBan: Has(Permission.BanMembers),
            CanTimeout: Has(Permission.TimeoutMembers),
            CanViewAuditLog: Has(Permission.ViewAuditLog),
            CanManageNicknames: Has(Permission.ManageNicknames),
            CanMuteMembers: Has(Permission.MuteMembers),
            CanDeafenMembers: Has(Permission.DeafenMembers),
            CanMoveMembers: Has(Permission.MoveMembers)
        ));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GuildResponse ToResponse(Guild g) =>
        new(g.Id, g.Name, g.Description, g.OwnerId, g.IconKey, g.BannerKey,
            g.IsPublic, g.MemberCount, g.CreatedAt,
            g.WelcomeChannelId, g.WelcomeMessage, g.SystemMessagesEnabled, g.RequireVerifiedEmail);

    private static GuildMemberResponse ToMemberResponse(GuildMember m, IEnumerable<long> roleIds) =>
        new(m.UserId, m.User.UserName!, m.Nickname,
            m.User.AvatarKey, m.IsOwner, m.JoinedAt, m.CommunicationDisabledUntil, roleIds);
}