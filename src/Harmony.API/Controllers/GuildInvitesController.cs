using System.Security.Claims;
using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
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
    private readonly IHubBroadcaster _broadcaster;
    private readonly IFriendRepository _friends;
    private readonly IDirectMessageRepository _dms;
    private readonly IMessageService _messages;
    private readonly INotificationService _notifications;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly string _clientUrl;
    private readonly ILogger<GuildInvitesController> _logger;

    public GuildInvitesController(
        IGuildInviteRepository invites,
        IChannelRepository channels,
        IUserRepository users,
        IAuditLogService audit,
        IPermissionService permissions,
        IHubBroadcaster broadcaster,
        IFriendRepository friends,
        IDirectMessageRepository dms,
        IMessageService messages,
        INotificationService notifications,
        ISnowflakeIdGenerator snowflake,
        IConfiguration configuration,
        ILogger<GuildInvitesController> logger
    )
    {
        _invites = invites;
        _channels = channels;
        _users = users;
        _audit = audit;
        _permissions = permissions;
        _broadcaster = broadcaster;
        _friends = friends;
        _dms = dms;
        _messages = messages;
        _notifications = notifications;
        _snowflake = snowflake;
        _clientUrl = (configuration["ClientUrl"] ?? "http://localhost:4200").TrimEnd('/');
        _logger = logger;
    }

    /// <summary>
    /// Mints a new invite for the guild, optionally landing on a specific channel, with optional
    /// max-uses and expiry.
    /// </summary>
    /// <remarks>
    /// Authorized as <c>CreateInvite OR ManageInvites</c> — in code, not via the usual route
    /// attribute. <c>ManageInvites</c> is a superset (a moderator who can revoke anyone's invite can
    /// obviously mint one), and the declarative <c>[RequirePermission]</c> filter can only AND bits,
    /// never OR them, so the check lives in the method body.
    /// </remarks>
    /// <response code="200">Created; body is the invite.</response>
    /// <response code="400">A named landing channel doesn't belong to this guild.</response>
    /// <response code="403">The caller has neither CreateInvite nor ManageInvites.</response>
    [HttpPost]
    [ProducesResponseType(typeof(InviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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

        var invite = await MintInviteAsync(
            guildId,
            userId,
            request.ChannelId,
            request.MaxUses,
            request.ExpiresInSeconds
        );

        var creators = await _users.GetByIdsAsync(new[] { userId });
        return Ok(ToResponse(invite, creators));
    }

    /// <summary>
    /// Invites a friend to the guild in one call: mints an invite, DMs its link to the friend, and
    /// files a <c>guild_invite</c> notification.
    /// </summary>
    /// <remarks>
    /// Done entirely server-side on purpose (NON-NEGOTIABLE #8: never trust a client's "I invited X"
    /// claim — the server mints, sends, and notifies so the whole thing is one authorable action).
    /// The link is posted as a full <c>…/invite/{code}</c> URL so the recipient's client renders the
    /// inline invite embed. Authorized like <c>Create</c> (CreateInvite OR ManageInvites); the target
    /// must be an accepted friend of the caller.
    /// </remarks>
    /// <response code="200">Invited; body is the minted invite.</response>
    /// <response code="400">Inviting yourself, or the target isn't an accepted friend.</response>
    /// <response code="403">The caller has neither CreateInvite nor ManageInvites.</response>
    [HttpPost("invite-friend")]
    [ProducesResponseType(typeof(InviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> InviteFriend(
        long guildId,
        [FromBody] InviteFriendRequest request
    )
    {
        var userId = GetUserId();

        if (
            !await _permissions.HasAsync(userId, guildId, Permission.CreateInvite)
            && !await _permissions.HasAsync(userId, guildId, Permission.ManageInvites)
        )
            return Forbid();

        if (request.FriendId == userId)
            return BadRequest(new { error = "You can't invite yourself." });

        var friendship = await _friends.GetBetweenAsync(userId, request.FriendId);
        if (friendship is null || friendship.Status != "accepted")
            return BadRequest(new { error = "You can only invite a friend." });

        // Guild-level invite (no landing channel) — the recipient joins at the guild's default.
        var invite = await MintInviteAsync(
            guildId,
            userId,
            channelId: null,
            request.MaxUses,
            request.ExpiresInSeconds
        );

        // Get-or-create the 1:1 DM, then unhide the caller's side so the invite thread surfaces.
        var dmChannelId = await _dms.GetSharedChannelIdAsync(userId, request.FriendId);
        if (dmChannelId is null)
        {
            var newChannelId = _snowflake.NextId();
            await _dms.CreateAsync(
                newChannelId,
                userId,
                request.FriendId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );
            dmChannelId = newChannelId;
        }
        await _dms.SetHiddenAsync(dmChannelId.Value, userId, false);

        // Post the full invite link so the recipient's client renders the inline invite embed
        // (the detection layer keys on a "…/invite/{code}" URL — §5.27 #9).
        var link = $"{_clientUrl}/invite/{invite.Code}";
        await _messages.SendMessageAsync(
            userId,
            guildId: null,
            dmChannelId.Value,
            new SendMessageRequest(link)
        );

        await _notifications.CreateGuildInviteNotificationAsync(request.FriendId, userId, guildId);

        var creators = await _users.GetByIdsAsync(new[] { userId });
        return Ok(ToResponse(invite, creators));
    }

    /// <summary>
    /// Lists the guild's invites — scoped to what the caller may see.
    /// </summary>
    /// <remarks>
    /// <c>ManageInvites</c> sees every invite in the guild; a <c>CreateInvite</c>-only member sees
    /// only the ones they personally minted. This mirrors the create/revoke OR-pattern and lets a
    /// plain member view — not just blindly revoke — their own invites, which the invite modal needs
    /// to render.
    /// </remarks>
    /// <response code="200">The invites the caller is allowed to see.</response>
    /// <response code="403">The caller has neither CreateInvite nor ManageInvites.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<InviteResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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

    /// <summary>
    /// Revokes an invite by code.
    /// </summary>
    /// <remarks>
    /// Authorized as <c>you created it OR you hold ManageInvites</c> (mirrors own-message delete): a
    /// <c>CreateInvite</c>-only member can revoke the invites they minted, and <c>ManageInvites</c>
    /// lets a moderator revoke anyone's. The delete is scoped to this guild — a code is globally
    /// unique but the route isn't, so an invite belonging to another guild returns 404 here rather
    /// than being revocable through the wrong route. The logged audit entry masks the code (holding
    /// it is enough to join, and ViewAuditLog is a separate permission from invite rights).
    /// </remarks>
    /// <response code="204">Revoked.</response>
    /// <response code="403">Not the creator and lacking ManageInvites.</response>
    /// <response code="404">No such code in this guild.</response>
    [HttpDelete("{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
            changes: new { code = MaskInviteCode(code) }
        );

        await BroadcastInvitesChangedAsync(guildId);

        return NoContent();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Mints, persists, audits, and broadcasts a guild invite. Shared by <see cref="Create"/> and
    /// <see cref="InviteFriend"/> so both paths produce identical rows/audit/broadcast. The caller
    /// is responsible for authorization and (if a channel is named) validating it belongs to the guild.
    /// </summary>
    private async Task<GuildInvite> MintInviteAsync(
        long guildId,
        long userId,
        long? channelId,
        int? maxUses,
        long? expiresInSeconds
    )
    {
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
            ChannelId = channelId,
            CreatorId = userId,
            MaxUses = maxUses,
            UseCount = 0,
            ExpiresAt = expiresInSeconds is { } secs ? now + secs * 1000 : null,
            CreatedAt = now,
        };

        await _invites.AddAsync(invite);
        await _invites.SaveChangesAsync();

        await _audit.LogAsync(
            guildId,
            userId,
            AuditLogAction.InviteCreate,
            targetId: channelId,
            changes: new
            {
                code = MaskInviteCode(code),
                channelId,
                maxUses,
                expiresAt = invite.ExpiresAt,
            }
        );

        await BroadcastInvitesChangedAsync(guildId);

        return invite;
    }

    /// <summary>
    /// Best-effort "invites changed" nudge to the guild group so any open invite modal refetches.
    /// A broadcast failure must never fail the already-committed create/revoke.
    /// </summary>
    private async Task BroadcastInvitesChangedAsync(long guildId)
    {
        try
        {
            await _broadcaster.BroadcastGuildInvitesChangedAsync(guildId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast GuildInvitesChanged for guild {GuildId}", guildId);
        }
    }

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

    /// <summary>
    /// An invite code is a bearer credential — holding it is enough to join. Viewing the audit log
    /// (ViewAuditLog) is a *separate* permission from minting or listing invites (CreateInvite /
    /// ManageInvites), so logging the raw code would hand a working invite to someone the guild
    /// deliberately withheld invite rights from. Keep a short prefix so an admin can still match the
    /// entry against the real invite list, and redact the rest — the remainder is unguessable.
    /// </summary>
    private static string MaskInviteCode(string code) =>
        code.Length <= VisibleCodeChars ? "•••" : $"{code[..VisibleCodeChars]}•••••";

    private const int VisibleCodeChars = 3;
}
