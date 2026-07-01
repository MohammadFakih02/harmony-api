using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Direct messages — guild-less conversations, 1:1 or group. Channel management
/// (open/list/hide, plus group create/add/leave) and the messaging surface, which
/// delegates to the shared IMessageService with a null guildId so the same persist →
/// broadcast pipeline is reused; only authorization differs (participant + not-blocked
/// instead of guild permissions).
/// </summary>
[ApiController]
[Route("api/dm")]
[Authorize]
[EnableRateLimiting("api")]
public class DirectMessagesController : ControllerBase
{
    private const string GroupDmType = "group_dm";

    /// <summary>Max total members in a group DM (Discord parity).</summary>
    private const int MaxGroupParticipants = 10;

    private readonly IDirectMessageRepository _dms;
    private readonly IUserRepository _users;
    private readonly IUserBlockRepository _blocks;
    private readonly IFriendRepository _friends;
    private readonly IChannelRepository _channels;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IMessageService _messageService;
    private readonly IUnreadCountService _unread;
    private readonly IFileService _files;
    private readonly IHubBroadcaster _broadcaster;

    public DirectMessagesController(
        IDirectMessageRepository dms,
        IUserRepository users,
        IUserBlockRepository blocks,
        IFriendRepository friends,
        IChannelRepository channels,
        ISnowflakeIdGenerator snowflake,
        IMessageService messageService,
        IUnreadCountService unread,
        IFileService files,
        IHubBroadcaster broadcaster
    )
    {
        _dms = dms;
        _users = users;
        _blocks = blocks;
        _friends = friends;
        _channels = channels;
        _snowflake = snowflake;
        _messageService = messageService;
        _unread = unread;
        _files = files;
        _broadcaster = broadcaster;
    }

    // POST /api/dm — open or reuse a 1:1 DM with another user
    [HttpPost]
    public async Task<IActionResult> CreateOrGet([FromBody] CreateDirectMessageRequest request)
    {
        var me = GetUserId();
        if (request.TargetUserId == me)
            return BadRequest(new { error = "You cannot open a DM with yourself." });

        var target = await _users.GetByIdAsync(request.TargetUserId);
        if (target is null)
            return NotFound(new { error = "User not found." });

        if (await _blocks.AreBlockedAsync(me, request.TargetUserId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Unable to message this user." });

        var existing = await _dms.GetSharedChannelIdAsync(me, request.TargetUserId);
        long channelId;
        if (existing is { } id)
        {
            channelId = id;
            // Reopening a DM I had hidden brings it back into my list.
            await _dms.SetHiddenAsync(channelId, me, false);
        }
        else
        {
            // Opening a *new* conversation: honour the target's DM-privacy. "friends_only"
            // blocks strangers; an accepted friendship (either direction) always passes.
            // Existing conversations above are exempt — this only gates first contact.
            if (target.DmPrivacy == DmPrivacy.FriendsOnly)
            {
                var friendship = await _friends.GetBetweenAsync(me, request.TargetUserId);
                if (friendship is not { Status: "accepted" })
                    return StatusCode(
                        StatusCodes.Status403Forbidden,
                        new { error = "This user only accepts direct messages from friends." }
                    );
            }

            channelId = _snowflake.NextId();
            await _dms.CreateAsync(
                channelId,
                me,
                request.TargetUserId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );
        }

        return Ok(OneToOneResponse(channelId, target, lastReadId: 0));
    }

    // POST /api/dm/group — create a group DM with two or more other users
    [HttpPost("group")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupDmRequest request)
    {
        var me = GetUserId();

        var others = (request.UserIds ?? [])
            .Where(uid => uid != me)
            .Distinct()
            .ToList();

        if (others.Count < 2)
            return BadRequest(new { error = "A group needs at least two other people." });
        if (others.Count > MaxGroupParticipants - 1)
            return BadRequest(new { error = $"A group can have at most {MaxGroupParticipants} people." });

        var users = await _users.GetByIdsAsync(others);
        if (users.Count != others.Count)
            return BadRequest(new { error = "One or more users were not found." });

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length > 100)
            return BadRequest(new { error = "Group name is too long." });

        var channelId = _snowflake.NextId();
        var allParticipants = new List<long>(others) { me };
        await _dms.CreateGroupAsync(
            channelId,
            name,
            allParticipants,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        await NotifyParticipantsAsync(allParticipants, channelId);

        var participants = others
            .Select(uid => new DmParticipantResponse(uid, users[uid].UserName!, users[uid].AvatarKey))
            .ToList();
        return Ok(new DirectMessageChannelResponse(channelId, IsGroup: true, name, LastReadId: 0, participants));
    }

    // POST /api/dm/{channelId}/participants — add a user to a group DM (any participant may add)
    [HttpPost("{channelId:long}/participants")]
    public async Task<IActionResult> AddParticipant(
        long channelId,
        [FromBody] AddGroupParticipantRequest request
    )
    {
        var me = GetUserId();
        if (!await _dms.IsParticipantAsync(channelId, me))
            return Forbid();
        if (!await IsGroupAsync(channelId))
            return BadRequest(new { error = "You can only add people to a group conversation." });

        var target = await _users.GetByIdAsync(request.UserId);
        if (target is null)
            return NotFound(new { error = "User not found." });

        var current = await _dms.GetParticipantIdsAsync(channelId);
        if (current.Contains(request.UserId))
            return NoContent(); // already in the group — idempotent
        if (current.Count >= MaxGroupParticipants)
            return BadRequest(new { error = "This group is full." });

        await _dms.AddParticipantAsync(channelId, request.UserId);

        // Notify everyone who is now a member (existing + the new one) to resync.
        var recipients = current.Append(request.UserId).ToList();
        await NotifyParticipantsAsync(recipients, channelId);
        return NoContent();
    }

    // DELETE /api/dm/{channelId}/participants/me — leave a group DM
    [HttpDelete("{channelId:long}/participants/me")]
    public async Task<IActionResult> Leave(long channelId)
    {
        var me = GetUserId();
        if (!await _dms.IsParticipantAsync(channelId, me))
            return NotFound();
        if (!await IsGroupAsync(channelId))
            return BadRequest(new { error = "You can only leave a group conversation; hide a 1:1 DM instead." });

        // Capture the membership (including me) before removal so my other tabs are told too.
        var recipients = await _dms.GetParticipantIdsAsync(channelId);
        await _dms.RemoveParticipantAsync(channelId, me);
        await NotifyParticipantsAsync(recipients, channelId);
        return NoContent();
    }

    // GET /api/dm — the caller's non-hidden DM/group channels
    [HttpGet]
    public async Task<IActionResult> GetMyDms()
    {
        var me = GetUserId();
        var summaries = await _dms.GetVisibleForUserAsync(me);
        if (summaries.Count == 0)
            return Ok(Array.Empty<DirectMessageChannelResponse>());

        var participantsByChannel = await _dms.GetParticipantsForChannelsAsync(
            summaries.Select(s => s.ChannelId)
        );

        var otherIds = participantsByChannel
            .Values.SelectMany(ids => ids)
            .Where(id => id != me)
            .Distinct();
        var users = await _users.GetByIdsAsync(otherIds);

        var result = summaries.Select(s => BuildResponse(s, participantsByChannel, users, me));
        return Ok(result);
    }

    // PATCH /api/dm/{channelId}/hide — hide the DM from the caller's list
    [HttpPatch("{channelId:long}/hide")]
    public async Task<IActionResult> Hide(long channelId)
    {
        var me = GetUserId();
        if (!await _dms.IsParticipantAsync(channelId, me))
            return NotFound();

        await _dms.SetHiddenAsync(channelId, me, true);
        return NoContent();
    }

    // POST /api/dm/{channelId}/messages — send (authorization handled in the service)
    [HttpPost("{channelId:long}/messages")]
    public async Task<IActionResult> SendMessage(long channelId, [FromBody] SendMessageRequest request)
    {
        var response = await _messageService.SendMessageAsync(GetUserId(), guildId: null, channelId, request);
        return Ok(response);
    }

    // GET /api/dm/{channelId}/messages — history
    [HttpGet("{channelId:long}/messages")]
    public async Task<IActionResult> GetMessages(
        long channelId,
        [FromQuery] int limit = 50,
        [FromQuery] long? before = null,
        [FromQuery] long? around = null,
        [FromQuery] long? after = null
    )
    {
        var response = await _messageService.GetChannelMessagesAsync(
            GetUserId(),
            guildId: null,
            channelId,
            limit,
            before,
            around,
            after
        );
        return Ok(response);
    }

    // PATCH /api/dm/{channelId}/messages/{messageId} — edit own message
    [HttpPatch("{channelId:long}/messages/{messageId:long}")]
    public async Task<IActionResult> EditMessage(
        long channelId,
        long messageId,
        [FromBody] EditMessageRequest request
    )
    {
        await _messageService.EditMessageAsync(GetUserId(), guildId: null, channelId, messageId, request);
        return NoContent();
    }

    // DELETE /api/dm/{channelId}/messages/{messageId} — delete own message
    [HttpDelete("{channelId:long}/messages/{messageId:long}")]
    public async Task<IActionResult> DeleteMessage(long channelId, long messageId)
    {
        await _messageService.DeleteMessageAsync(GetUserId(), guildId: null, channelId, messageId);
        return NoContent();
    }

    // GET /api/dm/{channelId}/pins — list pins (participation checked in the service)
    [HttpGet("{channelId:long}/pins")]
    public async Task<IActionResult> GetPins(long channelId, CancellationToken ct)
    {
        var pins = await _messageService.GetPinsAsync(GetUserId(), guildId: null, channelId, ct);
        return Ok(pins);
    }

    // PUT /api/dm/{channelId}/pins/{messageId} — pin (any participant)
    [HttpPut("{channelId:long}/pins/{messageId:long}")]
    public async Task<IActionResult> Pin(long channelId, long messageId, CancellationToken ct)
    {
        await _messageService.PinMessageAsync(GetUserId(), guildId: null, channelId, messageId, ct);
        return NoContent();
    }

    // DELETE /api/dm/{channelId}/pins/{messageId} — unpin (any participant)
    [HttpDelete("{channelId:long}/pins/{messageId:long}")]
    public async Task<IActionResult> Unpin(long channelId, long messageId, CancellationToken ct)
    {
        await _messageService.UnpinMessageAsync(GetUserId(), guildId: null, channelId, messageId, ct);
        return NoContent();
    }

    // POST /api/dm/{channelId}/files/presign — mint a presigned PUT (participant-gated)
    [HttpPost("{channelId:long}/files/presign")]
    public async Task<IActionResult> Presign(
        long channelId,
        [FromBody] PresignFileRequest request,
        CancellationToken ct
    )
    {
        var me = GetUserId();
        if (!await _dms.IsParticipantAsync(channelId, me))
            return Forbid();

        var response = await _files.PresignAsync(me, guildId: null, channelId, request, ct);
        return Ok(response);
    }

    // POST /api/dm/{channelId}/files/{fileId}/confirm — owner-gated in the service
    [HttpPost("{channelId:long}/files/{fileId:long}/confirm")]
    public async Task<IActionResult> ConfirmFile(long fileId, CancellationToken ct)
    {
        var response = await _files.ConfirmAsync(GetUserId(), fileId, ct);
        return Ok(response);
    }

    // GET /api/dm/{channelId}/files/{fileId} — presigned GET (participant-gated)
    [HttpGet("{channelId:long}/files/{fileId:long}")]
    public async Task<IActionResult> GetFileUrl(long channelId, long fileId, CancellationToken ct)
    {
        var me = GetUserId();
        if (!await _dms.IsParticipantAsync(channelId, me))
            return Forbid();

        var response = await _files.GetDownloadUrlAsync(guildId: null, channelId, fileId, ct);
        Response.Headers.CacheControl = "private, max-age=840";
        return Ok(response);
    }

    // POST /api/dm/{channelId}/read — mark the DM read
    [HttpPost("{channelId:long}/read")]
    public async Task<IActionResult> MarkRead(long channelId, [FromBody] MarkReadRequest request)
    {
        var me = GetUserId();
        if (!await _dms.IsParticipantAsync(channelId, me))
            return Forbid();

        await _unread.MarkReadAsync(me, guildId: null, channelId, request.LastReadMessageId);
        return NoContent();
    }

    private async Task<bool> IsGroupAsync(long channelId)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        return channel?.Type == GroupDmType;
    }

    private Task NotifyParticipantsAsync(IReadOnlyList<long> userIds, long channelId) =>
        _broadcaster.BroadcastDmChannelUpdatedAsync(userIds, new DmChannelUpdatedPayload(channelId));

    private static DirectMessageChannelResponse BuildResponse(
        DmChannelSummary summary,
        Dictionary<long, List<long>> participantsByChannel,
        Dictionary<long, User> users,
        long me
    )
    {
        var isGroup = summary.Type == GroupDmType;
        var others = participantsByChannel.TryGetValue(summary.ChannelId, out var ids)
            ? ids.Where(id => id != me)
            : Enumerable.Empty<long>();

        var participants = others
            .Where(users.ContainsKey)
            .Select(id => new DmParticipantResponse(id, users[id].UserName!, users[id].AvatarKey))
            .ToList();

        return new DirectMessageChannelResponse(
            summary.ChannelId,
            isGroup,
            isGroup ? summary.Name : null,
            summary.LastReadId,
            participants
        );
    }

    private static DirectMessageChannelResponse OneToOneResponse(long channelId, User peer, long lastReadId) =>
        new(
            channelId,
            IsGroup: false,
            Name: null,
            lastReadId,
            new List<DmParticipantResponse> { new(peer.Id, peer.UserName!, peer.AvatarKey) }
        );

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
