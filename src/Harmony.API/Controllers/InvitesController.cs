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
    private readonly IPresenceService _presence;
    private readonly IHubBroadcaster _broadcaster;
    private readonly INotificationService _notifications;
    private readonly ILogger<InvitesController> _logger;

    public InvitesController(
        IGuildInviteRepository invites,
        IGuildRepository guilds,
        IGuildBanRepository bans,
        IChannelRepository channels,
        IMessageService messages,
        IUserRepository users,
        IPresenceService presence,
        IHubBroadcaster broadcaster,
        INotificationService notifications,
        ILogger<InvitesController> logger
    )
    {
        _invites = invites;
        _guilds = guilds;
        _bans = bans;
        _channels = channels;
        _messages = messages;
        _users = users;
        _presence = presence;
        _broadcaster = broadcaster;
        _notifications = notifications;
        _logger = logger;
    }

    // Count of guild members currently online — resolved server-side (the viewer needn't be a
    // member). Everything that isn't a "showing" status (offline / invisible) is not counted.
    private async Task<int> OnlineCountAsync(long guildId)
    {
        var memberIds = await _guilds.GetMemberIdsAsync(guildId);
        if (memberIds.Count == 0)
            return 0;
        var statuses = await _presence.GetStatusesAsync(memberIds);
        return statuses.Values.Count(s =>
            s != PresenceStatus.Offline && s != PresenceStatus.Invisible
        );
    }

    /// <summary>
    /// Previews an invite before joining — the guild's name, icon, member count, and current online
    /// count — without joining. The online count is resolved server-side, so a non-member viewer
    /// still sees it.
    /// </summary>
    /// <param name="code">The invite code (from a <c>/invite/{code}</c> link).</param>
    /// <response code="200">The invite is valid; body is the guild preview.</response>
    /// <response code="404">No such invite code.</response>
    /// <response code="410">The invite has expired or been used up.</response>
    [HttpGet("{code}")]
    [ProducesResponseType(typeof(InvitePreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
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
            new InvitePreviewResponse(invite.Code, guild.Id, guild.Name, guild.IconKey, guild.MemberCount, await OnlineCountAsync(guild.Id), invite.ChannelId)
        );
    }

    /// <summary>
    /// Soft preview for inline chat embeds: renders the little invite card when a message contains an
    /// invite link. <b>Always 200</b> — the outcome is carried in a status field
    /// (<c>ok</c> / <c>expired</c> / <c>invalid</c>), never in the HTTP status.
    /// </summary>
    /// <remarks>
    /// The always-200 contract is deliberate. Dead codes in old messages are an expected, permanent
    /// state, and a real 404/410 here would log a browser console error for every expired invite link
    /// in visible history. The regular <c>Preview</c> endpoint, which drives the standalone landing
    /// page, still returns honest status codes.
    /// </remarks>
    /// <response code="200">Always. Inspect the <c>status</c> field for the real outcome.</response>
    [HttpGet("{code}/embed")]
    [ProducesResponseType(typeof(InviteEmbedResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewEmbed(string code)
    {
        var invite = await _invites.GetByCodeAsync(code);
        if (invite is null)
            return Ok(new InviteEmbedResponse("invalid", null));
        if (!IsAlive(invite))
            return Ok(new InviteEmbedResponse("expired", null));

        var guild = await _guilds.GetByIdAsync(invite.GuildId);
        if (guild is null)
            return Ok(new InviteEmbedResponse("invalid", null));

        return Ok(
            new InviteEmbedResponse(
                "ok",
                new InvitePreviewResponse(invite.Code, guild.Id, guild.Name, guild.IconKey, guild.MemberCount, await OnlineCountAsync(guild.Id), invite.ChannelId)
            )
        );
    }

    /// <summary>
    /// Redeems an invite and joins the caller to the guild. One click — no confirmation step.
    /// </summary>
    /// <remarks>
    /// Enforced in order: the invite must be alive, the caller must not already be a member (409, so
    /// the client just navigates in), must not be banned (403 with the ban reason), and must have a
    /// verified email if the guild requires one. On success the use count is bumped, the denormalized
    /// member count is adjusted atomically (not read-modify-write — two simultaneous joins would lose
    /// one), and a welcome message + a live <c>MemberJoined</c> broadcast fire best-effort.
    /// </remarks>
    /// <response code="200">Joined; body is the guild.</response>
    /// <response code="403">Banned from the guild, or a verified email is required.</response>
    /// <response code="404">No such invite, or its guild no longer exists.</response>
    /// <response code="409">Already a member — the client navigates straight in.</response>
    /// <response code="410">The invite has expired or been used up.</response>
    [HttpPost("{code}/join")]
    [ProducesResponseType(typeof(GuildResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
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
        invite.UseCount++; // tracked on the shared request DbContext; persisted by the save below

        await _guilds.SaveChangesAsync();

        // The membership row is the truth; MemberCount is a denormalized display counter, so bump
        // it atomically after the join lands rather than read-modify-writing the tracked entity
        // (two simultaneous joins would otherwise each write count+1 and lose one).
        await _guilds.AdjustMemberCountAsync(guild.Id, 1);

        await PostWelcomeMessageAsync(guild, userId, _channels, _messages, _logger);
        await BroadcastMemberJoinedAsync(guild.Id, userId, member.JoinedAt, _users, _broadcaster, _logger);

        // Best-effort: the redeem bumped the invite's use count, so any open invite modal refetches.
        try
        {
            await _broadcaster.BroadcastGuildInvitesChangedAsync(guild.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast GuildInvitesChanged for guild {GuildId}", guild.Id);
        }

        // Now that they're in, an outstanding guild_invite bell notification for this guild has served
        // its purpose — clear it (any invite path: the inline card, the bell, or the landing page).
        // Best-effort: the join is already committed, so a notification hiccup must never fail it.
        try
        {
            await _notifications.MarkGuildInviteNotificationsReadAsync(userId, guild.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear guild_invite notification for user {UserId} joining guild {GuildId}", userId, guild.Id);
        }

        // The atomic bump above bypassed the change tracker, so the loaded entity still holds the
        // pre-join count. Report the joiner's own arrival without re-reading (and without dirtying
        // the tracked property, which a later SaveChanges on this scope would double-count).
        return Ok(ToResponse(guild) with { MemberCount = guild.MemberCount + 1 });
    }

    /// <summary>
    /// Best-effort member-join system message. Posts to the configured welcome channel (or the
    /// first text channel by position) when the guild has system messages enabled. A failure here
    /// must never turn a successful join into an error — the join is already committed.
    /// Internal static so every join path (invite redeem, public-guild join) shares it.
    /// </summary>
    internal static async Task PostWelcomeMessageAsync(
        Guild guild,
        long joinerId,
        IChannelRepository channels,
        IMessageService messages,
        ILogger logger
    )
    {
        if (!guild.SystemMessagesEnabled)
            return;

        try
        {
            var targetChannelId = guild.WelcomeChannelId;
            if (targetChannelId is null)
            {
                var guildChannels = await channels.GetByGuildIdAsync(guild.Id);
                targetChannelId = guildChannels
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

            await messages.PublishSystemMessageAsync(
                guild.Id,
                channelId,
                joinerId,
                "member_join",
                content
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
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
    /// Internal static so every join path (invite redeem, public-guild join) shares it.
    /// </summary>
    internal static async Task BroadcastMemberJoinedAsync(
        long guildId,
        long userId,
        long joinedAt,
        IUserRepository users,
        IHubBroadcaster broadcaster,
        ILogger logger
    )
    {
        try
        {
            var user = await users.GetByIdAsync(userId);
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
            await broadcaster.BroadcastMemberJoinedAsync(
                guildId,
                new MemberJoinedPayload(guildId, member)
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
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
            g.WelcomeChannelId, g.WelcomeMessage, g.SystemMessagesEnabled, g.RequireVerifiedEmail);
}
