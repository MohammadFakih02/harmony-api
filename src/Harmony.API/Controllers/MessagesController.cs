using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Sending, reading, editing, and deleting messages in a guild channel.
/// </summary>
/// <remarks>
/// <para>
/// Sending here is a REST <b>fallback</b>. The primary send path is <c>ChatHub.SendMessage</c> over
/// SignalR; the client falls back to this endpoint only when the socket is down. Both paths converge
/// on the same <c>IMessageService</c>, which persists to ScyllaDB and publishes to RabbitMQ — the
/// consumer, not the hub, does the live fan-out (architecture non-negotiable: never broadcast
/// straight from the hub, always after persistence).
/// </para>
/// <para>
/// The response carries the client-supplied <c>nonce</c> back untouched. The client renders an
/// optimistic bubble immediately and reconciles it against the echo by matching that nonce first, so
/// the message settles correctly no matter whether the socket echo or this HTTP response wins the
/// race.
/// </para>
/// </remarks>
[ApiController]
[Route("api/guilds/{guildId}/channels/{channelId}/messages")]
[Authorize]
[EnableRateLimiting("api")]
public class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;

    public MessagesController(IMessageService messageService)
    {
        _messageService = messageService;
    }

    /// <summary>
    /// Sends a message to the channel (REST fallback for when the SignalR socket is down).
    /// </summary>
    /// <remarks>
    /// The request may carry attachment IDs and a reply-to ID. The response echoes the caller's nonce
    /// for optimistic-send reconciliation. Requires <c>SendMessages</c> on the channel — enforced in
    /// the service, which also resolves mentions and gates on send permission.
    /// </remarks>
    /// <response code="200">Sent; body carries the persisted message and the echoed nonce.</response>
    /// <response code="401">No valid user on the request.</response>
    /// <response code="403">The caller can't send in this channel.</response>
    [HttpPost]
    [ProducesResponseType(typeof(SendMessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SendMessage(
        long guildId,
        long channelId,
        [FromBody] SendMessageRequest request
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var response = await _messageService.SendMessageAsync(
            userId.Value,
            guildId,
            channelId,
            request
        );
        return Ok(response);
    }

    /// <summary>
    /// Forwards an existing message into this channel as a server-built, attributed snapshot.
    /// </summary>
    /// <remarks>
    /// The client sends only references; the server reads the original, authorizes that the caller
    /// may view the source, and builds the authoritative snapshot (author, timestamp, preview). Any
    /// attachments are re-uploaded as the new message's own — the forward doesn't alias the
    /// original's storage. This is why forwarding is a dedicated endpoint and not a client-side
    /// re-send (NON-NEGOTIABLE #8: never trust the client to describe what it's quoting).
    /// </remarks>
    /// <response code="200">Forwarded; body is the new message.</response>
    /// <response code="401">No valid user on the request.</response>
    /// <response code="403">The caller can't view the source or can't send here.</response>
    [HttpPost("forward")]
    [ProducesResponseType(typeof(SendMessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ForwardMessage(
        long guildId,
        long channelId,
        [FromBody] ForwardMessageRequest request
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var response = await _messageService.ForwardMessageAsync(
            userId.Value,
            guildId,
            channelId,
            request
        );
        return Ok(response);
    }

    /// <summary>
    /// Reads a page of messages from the channel. Supports three cursors for the different scroll
    /// modes.
    /// </summary>
    /// <remarks>
    /// The cursors are mutually exclusive: <c>before</c> paginates backward into history (the default
    /// scroll-up), <c>after</c> paginates forward, and <c>around</c> centres a page on a specific
    /// message — used by jump-to-message so a search hit or a reply link lands mid-history with
    /// context on both sides.
    /// </remarks>
    /// <param name="limit">Page size (default 50).</param>
    /// <param name="before">Return messages older than this message ID.</param>
    /// <param name="around">Centre the page on this message ID.</param>
    /// <param name="after">Return messages newer than this message ID.</param>
    /// <response code="200">A page of messages.</response>
    /// <response code="401">No valid user on the request.</response>
    /// <response code="403">The caller can't view this channel.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ChannelMessagesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMessages(
        long guildId,
        long channelId,
        [FromQuery] int limit = 50,
        [FromQuery] long? before = null,
        [FromQuery] long? around = null,
        [FromQuery] long? after = null
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var response = await _messageService.GetChannelMessagesAsync(
            userId.Value,
            guildId,
            channelId,
            limit,
            before,
            around,
            after
        );
        return Ok(response);
    }

    /// <summary>
    /// Deletes a message. The author may delete their own; a moderator with <c>ManageMessages</c> may
    /// delete anyone's (which also files an audit entry).
    /// </summary>
    /// <response code="204">Deleted.</response>
    /// <response code="401">No valid user on the request.</response>
    /// <response code="403">Not the author and lacking ManageMessages.</response>
    [HttpDelete("{messageId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteMessage(long guildId, long channelId, long messageId)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        await _messageService.DeleteMessageAsync(userId.Value, guildId, channelId, messageId);
        return NoContent();
    }

    /// <summary>
    /// Edits a message's content. Author-only; re-resolves mentions and re-indexes for search.
    /// </summary>
    /// <response code="204">Edited.</response>
    /// <response code="401">No valid user on the request.</response>
    /// <response code="403">Not the author.</response>
    [HttpPatch("{messageId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> EditMessage(
        long guildId,
        long channelId,
        long messageId,
        [FromBody] EditMessageRequest request
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        await _messageService.EditMessageAsync(
            userId.Value,
            guildId,
            channelId,
            messageId,
            request
        );
        return NoContent();
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
