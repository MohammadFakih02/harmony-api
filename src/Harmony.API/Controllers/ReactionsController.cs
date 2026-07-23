using System.Security.Claims;
using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Emoji-reaction endpoints for guild channels. Nested under the channel so the route-based
/// <see cref="RequirePermissionAttribute"/> applies the channel-scoped <see cref="Permission.AddReactions"/>
/// bit (overrides included). DM reactions live on <c>DirectMessagesController</c> (participant-gated).
/// The emoji travels in the request body (PUT) or query string (DELETE), never the route — Unicode in
/// a URL path segment is fragile. Adding is an idempotent upsert.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/channels/{channelId:long}/messages/{messageId:long}/reactions")]
[Authorize]
[EnableRateLimiting("api")]
public class ReactionsController : ControllerBase
{
    private readonly IMessageService _messages;

    public ReactionsController(IMessageService messages)
    {
        _messages = messages;
    }

    // PUT /api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions
    [HttpPut]
    [RequirePermission(Permission.AddReactions)]
    public async Task<IActionResult> Add(
        long guildId,
        long channelId,
        long messageId,
        [FromBody] ReactionRequest request,
        CancellationToken ct
    )
    {
        await _messages.AddReactionAsync(
            GetUserId(),
            guildId,
            channelId,
            messageId,
            request.Emoji,
            ct
        );
        return NoContent();
    }

    /// <summary>
    /// Removes the caller's reaction of a given emoji from a message. Idempotent — removing a
    /// reaction that isn't there is a no-op, not an error.
    /// </summary>
    /// <remarks>
    /// The emoji travels in the query string rather than the route: Unicode in a URL path segment
    /// is fragile across proxies and clients. Only the caller's own reaction is removed; a user
    /// cannot clear someone else's.
    /// </remarks>
    /// <param name="emoji">The emoji to remove (Unicode, or a <c>custom:{id}</c> token).</param>
    /// <response code="204">The reaction was removed, or was not present.</response>
    /// <response code="403">The caller lacks <c>AddReactions</c> on this channel.</response>
    // DELETE /api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions?emoji=
    [HttpDelete]
    [RequirePermission(Permission.AddReactions)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Remove(
        long guildId,
        long channelId,
        long messageId,
        [FromQuery] string emoji,
        CancellationToken ct
    )
    {
        await _messages.RemoveReactionAsync(GetUserId(), guildId, channelId, messageId, emoji, ct);
        return NoContent();
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
