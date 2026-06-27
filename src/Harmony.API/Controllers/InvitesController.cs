using System.Security.Claims;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Resolving an invite by its code — the guild-agnostic half of invites. Preview and redeem
/// take only a code (the guild is discovered from the row), so they can't be guild-scoped and
/// authorize manually (any authenticated user may follow a valid invite). Creation/listing/
/// revocation live on the guild-scoped <c>GuildInvitesController</c>.
/// </summary>
[ApiController]
[Route("api/invites")]
[Authorize]
[EnableRateLimiting("api")]
public class InvitesController : ControllerBase
{
    private readonly IGuildInviteRepository _invites;
    private readonly IGuildRepository _guilds;
    private readonly IGuildBanRepository _bans;

    public InvitesController(
        IGuildInviteRepository invites,
        IGuildRepository guilds,
        IGuildBanRepository bans
    )
    {
        _invites = invites;
        _guilds = guilds;
        _bans = bans;
    }

    // GET /api/invites/{code} — preview before joining.
    [HttpGet("{code}")]
    public async Task<IActionResult> Preview(string code)
    {
        var invite = await _invites.GetByCodeAsync(code);
        if (invite is null)
            return NotFound(new { error = "Invalid invite." });
        if (!IsAlive(invite))
            return StatusCode(StatusCodes.Status410Gone, new { error = "Invite expired or used up." });

        var guild = await _guilds.GetByIdAsync(invite.GuildId);
        if (guild is null)
            return NotFound(new { error = "Invalid invite." });

        return Ok(
            new InvitePreviewResponse(invite.Code, guild.Id, guild.Name, guild.IconKey, guild.MemberCount, invite.ChannelId)
        );
    }

    // POST /api/invites/{code}/join — redeem the invite and join the guild.
    [HttpPost("{code}/join")]
    public async Task<IActionResult> Join(string code)
    {
        var userId = GetUserId();

        var invite = await _invites.GetByCodeAsync(code);
        if (invite is null)
            return NotFound(new { error = "Invalid invite." });
        if (!IsAlive(invite))
            return StatusCode(StatusCodes.Status410Gone, new { error = "Invite expired or used up." });

        var guild = await _guilds.GetByIdAsync(invite.GuildId);
        if (guild is null)
            return NotFound(new { error = "Invalid invite." });

        if (await _guilds.IsMemberAsync(guild.Id, userId))
            return Conflict(new { error = "Already a member of this guild." });

        if (await _bans.IsBannedAsync(guild.Id, userId))
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "You are banned from this guild." }
            );

        var member = new GuildMember
        {
            UserId = userId,
            GuildId = guild.Id,
            IsOwner = false,
            JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _guilds.AddMemberAsync(member);
        guild.MemberCount++;
        invite.UseCount++; // tracked on the shared request DbContext; persisted by the save below

        await _guilds.SaveChangesAsync();

        return Ok(ToResponse(guild));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static bool IsAlive(GuildInvite invite)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (invite.ExpiresAt is { } e && e <= now)
            return false;
        if (invite.MaxUses is { } m && invite.UseCount >= m)
            return false;
        return true;
    }

    private static GuildResponse ToResponse(Guild g) =>
        new(g.Id, g.Name, g.Description, g.OwnerId, g.IconKey, g.BannerKey, g.IsPublic, g.MemberCount, g.CreatedAt);
}
