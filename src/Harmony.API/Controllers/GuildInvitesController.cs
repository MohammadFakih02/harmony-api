using System.Security.Claims;
using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Management of a guild's invites (create / list / revoke). Guild-scoped so the route-driven
/// <see cref="RequirePermissionAttribute"/> enforces access: creating needs
/// <see cref="Permission.CreateInvite"/>; listing/deleting need <see cref="Permission.ManageInvites"/>.
/// Redeeming and previewing a code live on the flat <c>InvitesController</c> (no guild known yet).
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/invites")]
[Authorize]
[EnableRateLimiting("api")]
public class GuildInvitesController : ControllerBase
{
    private readonly IGuildInviteRepository _invites;
    private readonly IChannelRepository _channels;
    private readonly IUserRepository _users;
    private readonly IAuditLogService _audit;
    private readonly IPermissionService _permissions;

    public GuildInvitesController(
        IGuildInviteRepository invites,
        IChannelRepository channels,
        IUserRepository users,
        IAuditLogService audit,
        IPermissionService permissions
    )
    {
        _invites = invites;
        _channels = channels;
        _users = users;
        _audit = audit;
        _permissions = permissions;
    }

    // POST /api/guilds/{guildId}/invites
    // No [RequirePermission] — creating is authorized in-body as "CreateInvite OR ManageInvites",
    // since ManageInvites is a superset of CreateInvite (a moderator who can revoke anyone's invite
    // can obviously mint one). [RequirePermission] can only AND bits, so the OR lives here.
    [HttpPost]
    public async Task<IActionResult> Create(long guildId, [FromBody] CreateInviteRequest request)
    {
        var userId = GetUserId();

        if (
            !await _permissions.HasAsync(userId, guildId, Permission.CreateInvite)
            && !await _permissions.HasAsync(userId, guildId, Permission.ManageInvites)
        )
            return Forbid();

        // A landing channel is optional, but if one is named it must belong to this guild.
        if (request.ChannelId is { } channelId)
        {
            var channel = await _channels.GetByIdAndGuildIdAsync(channelId, guildId);
            if (channel is null)
                return BadRequest(new { error = "Channel not found in this guild." });
        }

        // Codes are the table's primary key (globally unique), so retry until we mint a free one.
        string code;
        do
        {
            code = GenerateInviteCode();
        } while (await _invites.GetByCodeAsync(code) is not null);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var invite = new GuildInvite
        {
            Code = code,
            GuildId = guildId,
            ChannelId = request.ChannelId,
            CreatorId = userId,
            MaxUses = request.MaxUses,
            UseCount = 0,
            ExpiresAt = request.ExpiresInSeconds is { } secs ? now + secs * 1000 : null,
            CreatedAt = now,
        };

        await _invites.AddAsync(invite);
        await _invites.SaveChangesAsync();

        await _audit.LogAsync(
            guildId,
            userId,
            AuditLogAction.InviteCreate,
            targetId: request.ChannelId,
            changes: new
            {
                code,
                channelId = request.ChannelId,
                maxUses = request.MaxUses,
                expiresAt = invite.ExpiresAt,
            }
        );

        var creators = await _users.GetByIdsAsync(new[] { userId });
        return Ok(ToResponse(invite, creators));
    }

    // GET /api/guilds/{guildId}/invites
    // No [RequirePermission] — listing is authorized in-body: ManageInvites sees all of the guild's
    // invites, while a CreateInvite-only member sees only their own (mirrors the Create/Delete
    // OR-pattern above). This lets a plain member view — not just blindly revoke — the invites they
    // minted, which the modal needs to render.
    [HttpGet]
    public async Task<IActionResult> List(long guildId)
    {
        var userId = GetUserId();
        var canManage = await _permissions.HasAsync(userId, guildId, Permission.ManageInvites);
        if (!canManage && !await _permissions.HasAsync(userId, guildId, Permission.CreateInvite))
            return Forbid();

        var all = await _invites.GetByGuildAsync(guildId);
        var invites = canManage ? all : all.Where(i => i.CreatorId == userId).ToList();
        var creators = await _users.GetByIdsAsync(invites.Select(i => i.CreatorId).Distinct());
        return Ok(invites.Select(i => ToResponse(i, creators)));
    }

    // DELETE /api/guilds/{guildId}/invites/{code}
    // No [RequirePermission] — revoke is authorized in-body as "you created it OR you hold
    // ManageInvites", so a CreateInvite-only member can revoke their own invite (mirrors
    // own-message delete). ManageInvites lets a moderator revoke anyone's.
    [HttpDelete("{code}")]
    public async Task<IActionResult> Delete(long guildId, string code)
    {
        var invite = await _invites.GetByCodeAsync(code);

        // Scope the delete to this guild: a code is globally unique but the route isn't, so an
        // invite from another guild must not be revocable through this guild's route.
        if (invite is null || invite.GuildId != guildId)
            return NotFound();

        var userId = GetUserId();
        if (
            invite.CreatorId != userId
            && !await _permissions.HasAsync(userId, guildId, Permission.ManageInvites)
        )
            return Forbid();

        _invites.Remove(invite);
        await _invites.SaveChangesAsync();

        await _audit.LogAsync(
            guildId,
            GetUserId(),
            AuditLogAction.InviteDelete,
            targetId: invite.ChannelId,
            changes: new { code }
        );

        return NoContent();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static InviteResponse ToResponse(GuildInvite i, IReadOnlyDictionary<long, User> creators)
    {
        creators.TryGetValue(i.CreatorId, out var creator);
        return new InviteResponse(
            i.Code,
            i.GuildId,
            i.ChannelId,
            i.CreatorId,
            creator?.UserName,
            i.MaxUses,
            i.UseCount,
            i.ExpiresAt,
            i.CreatedAt
        );
    }

    // Random URL-safe 8-char code (relocated from GuildsController, which used it for the
    // now-removed permanent guild invite code).
    private static string GenerateInviteCode() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "")
            .Replace("+", "")
            .Replace("=", "")[..8];
}
