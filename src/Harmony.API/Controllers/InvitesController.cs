using System.Security.Claims;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

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
    private readonly IChannelRepository _channels;
    private readonly IMessageService _messages;
    private readonly IUserRepository _users;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<InvitesController> _logger;

    public InvitesController(
        IGuildInviteRepository invites,
        IGuildRepository guilds,
        IGuildBanRepository bans,
        IChannelRepository channels,
        IMessageService messages,
        IUserRepository users,
        IHubBroadcaster broadcaster,
        ILogger<InvitesController> logger
    )
    {
        _invites = invites;
        _guilds = guilds;
        _bans = bans;
        _channels = channels;
        _messages = messages;
        _users = users;
        _broadcaster = broadcaster;
        _logger = logger;
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

        await PostWelcomeMessageAsync(guild, userId);
        await BroadcastMemberJoinedAsync(guild.Id, userId, member.JoinedAt);

        return Ok(ToResponse(guild));
    }

    /// <summary>
    /// Best-effort member-join system message. Posts to the configured welcome channel (or the
    /// first text channel by position) when the guild has system messages enabled. A failure here
    /// must never turn a successful join into an error — the join is already committed.
    /// </summary>
    private async Task PostWelcomeMessageAsync(Guild guild, long joinerId)
    {
        if (!guild.SystemMessagesEnabled)
            return;

        try
        {
            var targetChannelId = guild.WelcomeChannelId;
            if (targetChannelId is null)
            {
                var channels = await _channels.GetByGuildIdAsync(guild.Id);
                targetChannelId = channels
                    .Where(c => c.Type == "text")
                    .OrderBy(c => c.Position)
                    .Select(c => (long?)c.Id)
                    .FirstOrDefault();
            }

            if (targetChannelId is not { } channelId)
                return; // no text channel to post into

            // Content carries the admin's greeting (if any); the "member_join" type + author
            // identity is what the client renders as "X joined". Empty content = plain join notice.
            var content = guild.WelcomeMessage ?? string.Empty;

            await _messages.PublishSystemMessageAsync(
                guild.Id,
                channelId,
                joinerId,
                "member_join",
                content
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to post welcome message for user {UserId} joining guild {GuildId}",
                joinerId,
                guild.Id
            );
        }
    }

    /// <summary>
    /// Best-effort: announce the join to the guild group so every connected member inserts the new
    /// member into their list live. A fresh join has no roles/nickname/timeout yet. Failure here
    /// must never fail the join itself — the member is already persisted.
    /// </summary>
    private async Task BroadcastMemberJoinedAsync(long guildId, long userId, long joinedAt)
    {
        try
        {
            var user = await _users.GetByIdAsync(userId);
            var member = new GuildMemberResponse(
                userId,
                user?.UserName ?? "Unknown",
                Nickname: null,
                user?.AvatarKey,
                IsOwner: false,
                JoinedAt: joinedAt,
                CommunicationDisabledUntil: null,
                RoleIds: Array.Empty<long>()
            );
            await _broadcaster.BroadcastMemberJoinedAsync(
                guildId,
                new MemberJoinedPayload(guildId, member)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to broadcast MemberJoined for user {UserId} joining guild {GuildId}",
                userId,
                guildId
            );
        }
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
        new(g.Id, g.Name, g.Description, g.OwnerId, g.IconKey, g.BannerKey, g.IsPublic, g.MemberCount, g.CreatedAt,
            g.WelcomeChannelId, g.WelcomeMessage, g.SystemMessagesEnabled);
}
