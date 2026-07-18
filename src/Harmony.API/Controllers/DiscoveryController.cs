using System.Security.Claims;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Public-server discovery (§10 <c>Guilds.is_public</c> — the opt-in flag guild admins set on the
/// Overview pane). Browse lists only discoverable guilds; joining one needs no invite but runs the
/// same ban/membership checks (and welcome message + MemberJoined broadcast) as an invite redeem.
/// </summary>
[ApiController]
[Route("api/guilds")]
[Authorize]
[EnableRateLimiting("api")]
public class DiscoveryController : ControllerBase
{
    private const int MaxResults = 50;

    private readonly IGuildRepository _guilds;
    private readonly IGuildBanRepository _bans;
    private readonly IChannelRepository _channels;
    private readonly IMessageService _messages;
    private readonly IUserRepository _users;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<DiscoveryController> _logger;

    public DiscoveryController(
        IGuildRepository guilds,
        IGuildBanRepository bans,
        IChannelRepository channels,
        IMessageService messages,
        IUserRepository users,
        IHubBroadcaster broadcaster,
        ILogger<DiscoveryController> logger
    )
    {
        _guilds = guilds;
        _bans = bans;
        _channels = channels;
        _messages = messages;
        _users = users;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    // GET /api/guilds/discover?q=&limit= — browse discoverable guilds, biggest first.
    [HttpGet("discover")]
    public async Task<IActionResult> Discover([FromQuery] string? q = null, [FromQuery] int limit = 25)
    {
        limit = Math.Clamp(limit, 1, MaxResults);
        var guilds = await _guilds.GetPublicGuildsAsync(q, limit);
        return Ok(guilds.Select(ToResponse));
    }

    // POST /api/guilds/{guildId}/join — join a discoverable guild without an invite.
    [HttpPost("{guildId:long}/join")]
    public async Task<IActionResult> Join(long guildId)
    {
        var userId = GetUserId();

        var guild = await _guilds.GetByIdAsync(guildId);
        // A private guild is indistinguishable from a missing one — don't leak its existence.
        if (guild is null || !guild.IsPublic)
            return NotFound(new { error = "This server is not discoverable." });

        if (await _guilds.IsMemberAsync(guild.Id, userId))
            return Conflict(new { error = "Already a member of this guild." });

        if (await _bans.GetAsync(guild.Id, userId) is { } ban)
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new
                {
                    error = string.IsNullOrWhiteSpace(ban.Reason)
                        ? "You are banned from this guild."
                        : $"You are banned from this guild. Reason: {ban.Reason}",
                    banned = true,
                    reason = ban.Reason,
                }
            );

        if (guild.RequireVerifiedEmail)
        {
            var joiner = await _users.GetByIdAsync(userId);
            if (joiner is null || !joiner.EmailConfirmed)
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    new
                    {
                        error = "This server requires a verified email address.",
                        requiresVerifiedEmail = true,
                    }
                );
        }

        var member = new GuildMember
        {
            UserId = userId,
            GuildId = guild.Id,
            IsOwner = false,
            JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _guilds.AddMemberAsync(member);
        await _guilds.SaveChangesAsync();

        // See InvitesController.Join — atomic bump after the membership lands, so concurrent joins
        // can't lose each other's increment.
        await _guilds.AdjustMemberCountAsync(guild.Id, 1);

        await InvitesController.PostWelcomeMessageAsync(guild, userId, _channels, _messages, _logger);
        await InvitesController.BroadcastMemberJoinedAsync(
            guild.Id, userId, member.JoinedAt, _users, _broadcaster, _logger);

        // The tracked entity still holds the pre-join count (the bump bypassed the change tracker).
        return Ok(ToResponse(guild) with { MemberCount = guild.MemberCount + 1 });
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static GuildResponse ToResponse(Guild g) =>
        new(g.Id, g.Name, g.Description, g.OwnerId, g.IconKey, g.BannerKey, g.IsPublic, g.MemberCount, g.CreatedAt,
            g.WelcomeChannelId, g.WelcomeMessage, g.SystemMessagesEnabled, g.RequireVerifiedEmail);
}
