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

    public GuildInvitesController(
        IGuildInviteRepository invites,
        IChannelRepository channels,
        IUserRepository users,
        IAuditLogService audit
    )
    {
        _invites = invites;
        _channels = channels;
        _users = users;
        _audit = audit;
    }

    // POST /api/guilds/{guildId}/invites
    [HttpPost]
    [RequirePermission(Permission.CreateInvite)]
    public async Task<IActionResult> Create(long guildId, [FromBody] CreateInviteRequest request)
    {
        var userId = GetUserId();

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
    [HttpGet]
    [RequirePermission(Permission.ManageInvites)]
    public async Task<IActionResult> List(long guildId)
    {
        var invites = await _invites.GetByGuildAsync(guildId);
        var creators = await _users.GetByIdsAsync(invites.Select(i => i.CreatorId).Distinct());
        return Ok(invites.Select(i => ToResponse(i, creators)));
    }

    // DELETE /api/guilds/{guildId}/invites/{code}
    [HttpDelete("{code}")]
    [RequirePermission(Permission.ManageInvites)]
    public async Task<IActionResult> Delete(long guildId, string code)
    {
        var invite = await _invites.GetByCodeAsync(code);

        // Scope the delete to this guild: holding ManageInvites here must not let you revoke an
        // invite that belongs to a different guild (codes are globally unique, the route isn't).
        if (invite is null || invite.GuildId != guildId)
            return NotFound();

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
