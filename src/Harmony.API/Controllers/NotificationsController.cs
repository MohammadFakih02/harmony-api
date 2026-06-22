using System.Security.Claims;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Read/mark-read side of notifications: list, unread count, mark-one-read, mark-all-read.
/// Creation lives in INotificationService (consumed by MessageConsumerHandler for mentions
/// and FriendsController for friend requests) — this controller only ever reads or mutates
/// IsRead on rows that already exist; it has no AreBlockedAsync/IsMutedAsync of its own,
/// because that suppression already happened before a row was persisted.
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
[EnableRateLimiting("api")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationRepository _notifications;

    public NotificationsController(INotificationRepository notifications)
    {
        _notifications = notifications;
    }

    // GET /api/notifications?limit=20 — most recent first.
    [HttpGet]
    public async Task<IActionResult> GetMyNotifications(int limit = 20)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        var notifications = await _notifications.GetForUserAsync(userId.Value, limit);
        return Ok(
            notifications.Select(n => new NotificationResponse(
                n.Id,
                n.Type,
                n.ActorId,
                n.GuildId,
                n.ChannelId,
                n.MessageId,
                n.IsRead,
                n.CreatedAt
            ))
        );
    }

    // GET /api/notifications/unread-count — for the badge; count only, no row fetch.
    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount()
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        var count = await _notifications.GetUnreadCountAsync(userId.Value);
        return Ok(count);
    }

    // PATCH /api/notifications/{id}/read — NotFound covers both "doesn't exist" and
    // "exists but isn't yours" (GetByIdForUserAsync bakes the ownership check into the
    // query), so this never leaks whether someone else's notification id is valid.
    [HttpPatch("{id}/read")]
    public async Task<IActionResult> MarkAsRead(long id)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        var notification = await _notifications.GetByIdForUserAsync(id, userId.Value);
        if (notification is null)
        {
            return NotFound();
        }
        notification.IsRead = true;
        await _notifications.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllAsRead()
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        await _notifications.MarkAllReadAsync(userId.Value);
        return NoContent();
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
