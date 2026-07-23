using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting("api")]
public class ReadStatesController : ControllerBase
{
    private readonly IUnreadCountService _unread;
    private readonly IGuildRepository _guilds;
    private readonly IChannelRepository _channels;
    private readonly INotificationService _notifications;

    public ReadStatesController(
        IUnreadCountService unread,
        IGuildRepository guilds,
        IChannelRepository channels,
        INotificationService notifications
    )
    {
        _unread = unread;
        _guilds = guilds;
        _channels = channels;
        _notifications = notifications;
    }

    // POST /api/guilds/{guildId}/channels/{channelId}/read
    [HttpPost("api/guilds/{guildId:long}/channels/{channelId:long}/read")]
    public async Task<IActionResult> MarkRead(
        long guildId,
        long channelId,
        [FromBody] MarkReadRequest request
    )
    {
        var userId = GetUserId();

        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();

        // Never trust client IDs — confirm the channel belongs to this guild.
        var channel = await _channels.GetByIdAndGuildIdAsync(channelId, guildId);
        if (channel is null)
            return NotFound();

        await _unread.MarkReadAsync(userId, guildId, channelId, request.LastReadMessageId);
        // Reading a channel also clears its bell entries (mentions/replies/all-level messages) up to
        // the read point — the badge and the bell stay consistent, and it catches rows the client
        // never loaded into the bell page (and other devices) via the fresh badge broadcast.
        await _notifications.MarkChannelNotificationsReadAsync(
            userId,
            channelId,
            request.LastReadMessageId
        );
        return NoContent();
    }

    // GET /api/users/me/unread
    [HttpGet("api/users/me/unread")]
    public async Task<IActionResult> GetUnread()
    {
        var userId = GetUserId();

        var guilds = await _guilds.GetByUserIdAsync(userId);
        var channelGuildMap = await _channels.GetTextChannelGuildMapAsync(guilds.Select(g => g.Id));
        var counts = await _unread.GetUnreadForUserAsync(userId, channelGuildMap.Keys);

        // Attach each channel's guildId so the client can roll counts up to guild badges.
        return Ok(
            counts.Select(kv => new UnreadCountResponse(
                kv.Key,
                channelGuildMap[kv.Key],
                kv.Value
            ))
        );
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
