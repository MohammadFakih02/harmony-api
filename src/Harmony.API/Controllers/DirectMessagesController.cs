using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Direct messages — guild-less 1:1 conversations. Channel management (open/list/hide)
/// plus the messaging surface, which delegates to the shared IMessageService with a null
/// guildId so the same persist → broadcast pipeline is reused; only authorization differs
/// (participant + not-blocked instead of guild permissions).
/// </summary>
[ApiController]
[Route("api/dm")]
[Authorize]
[EnableRateLimiting("api")]
public class DirectMessagesController : ControllerBase
{
    private readonly IDirectMessageRepository _dms;
    private readonly IUserRepository _users;
    private readonly IUserBlockRepository _blocks;
    private readonly IFriendRepository _friends;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IMessageService _messageService;
    private readonly IUnreadCountService _unread;
    private readonly IFileService _files;

    public DirectMessagesController(
        IDirectMessageRepository dms,
        IUserRepository users,
        IUserBlockRepository blocks,
        IFriendRepository friends,
        ISnowflakeIdGenerator snowflake,
        IMessageService messageService,
        IUnreadCountService unread,
        IFileService files
    )
    {
        _dms = dms;
        _users = users;
        _blocks = blocks;
        _friends = friends;
        _snowflake = snowflake;
        _messageService = messageService;
        _unread = unread;
        _files = files;
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

        return Ok(ToResponse(channelId, target, lastReadId: 0));
    }

    // GET /api/dm — the caller's non-hidden DM channels
    [HttpGet]
    public async Task<IActionResult> GetMyDms()
    {
        var me = GetUserId();
        var views = await _dms.GetVisibleForUserAsync(me);
        if (views.Count == 0)
            return Ok(Array.Empty<DirectMessageChannelResponse>());

        var peers = await _users.GetByIdsAsync(views.Select(v => v.PeerId));
        var result = views
            .Where(v => peers.ContainsKey(v.PeerId))
            .Select(v => ToResponse(v.ChannelId, peers[v.PeerId], v.LastReadId));

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
        [FromQuery] long? before = null
    )
    {
        var response = await _messageService.GetChannelMessagesAsync(
            GetUserId(),
            guildId: null,
            channelId,
            limit,
            before
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

    private static DirectMessageChannelResponse ToResponse(
        long channelId,
        Harmony.Domain.Domain.Entities.User peer,
        long lastReadId
    ) =>
        new(channelId, peer.Id, peer.UserName!, peer.AvatarKey, lastReadId);

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
