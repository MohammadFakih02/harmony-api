using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
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
    private readonly INotificationPreferenceRepository _preferences;
    private readonly IPushSubscriptionRepository _pushSubscriptions;
    private readonly IWebPushSender _webPush;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        INotificationRepository notifications,
        INotificationPreferenceRepository preferences,
        IPushSubscriptionRepository pushSubscriptions,
        IWebPushSender webPush,
        ISnowflakeIdGenerator snowflake,
        IHubBroadcaster broadcaster,
        ILogger<NotificationsController> logger
    )
    {
        _notifications = notifications;
        _preferences = preferences;
        _pushSubscriptions = pushSubscriptions;
        _webPush = webPush;
        _snowflake = snowflake;
        _broadcaster = broadcaster;
        _logger = logger;
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
        await BroadcastBadgeAsync(userId.Value);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllAsRead()
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        await _notifications.MarkAllReadAsync(userId.Value);
        await BroadcastBadgeAsync(userId.Value);
        return NoContent();
    }

    // DELETE /api/notifications/{id} — removes one notification. NotFound covers both
    // "doesn't exist" and "isn't yours" (the delete is scoped to the owner), so it never
    // leaks whether someone else's notification id is valid.
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(long id)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        var deleted = await _notifications.DeleteForUserAsync(id, userId.Value);
        if (deleted)
            await BroadcastBadgeAsync(userId.Value);
        return deleted ? NoContent() : NotFound();
    }

    // DELETE /api/notifications — clears every notification for the caller ("clear all").
    // Owner-scoped; idempotent (no-op when there's nothing to clear).
    [HttpDelete]
    public async Task<IActionResult> DeleteAll()
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        await _notifications.DeleteAllForUserAsync(userId.Value);
        await BroadcastBadgeAsync(userId.Value);
        return NoContent();
    }

    // After the caller mutates their own read/deleted state, push the fresh unread count so
    // other open tabs update the bell badge live. Best-effort — a broadcast failure must never
    // fail the already-committed mutation (the client also re-derives the count on next fetch).
    private async Task BroadcastBadgeAsync(long userId)
    {
        try
        {
            var count = await _notifications.GetUnreadCountAsync(userId);
            await _broadcaster.BroadcastNotificationBadgeAsync(userId, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast notification badge for user {UserId}", userId);
        }
    }

    // GET /api/notifications/preferences — the caller's toggles. A user with no row yet reads
    // all-true defaults (the same null-means-default contract the repository documents).
    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        var pref = await _preferences.GetAsync(userId.Value);
        return Ok(ToResponse(pref));
    }

    // PATCH /api/notifications/preferences — partial update; null flags are left unchanged.
    // Creates the row on first save (pre-feature users have none), mirroring the registration seed.
    [HttpPatch("preferences")]
    public async Task<IActionResult> UpdatePreferences(
        [FromBody] UpdateNotificationPreferenceRequest request
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var pref = await _preferences.GetAsync(userId.Value);
        if (pref is null)
        {
            pref = new NotificationPreference { UserId = userId.Value };
            await _preferences.AddAsync(pref);
        }

        if (request.MentionsEnabled is { } mentions)
            pref.MentionsEnabled = mentions;
        if (request.RepliesEnabled is { } replies)
            pref.RepliesEnabled = replies;
        if (request.FriendRequests is { } friends)
            pref.FriendRequests = friends;
        if (request.GuildInvites is { } invites)
            pref.GuildInvites = invites;
        if (request.PushEnabled is { } push)
            pref.PushEnabled = push;

        await _preferences.SaveChangesAsync();
        return Ok(ToResponse(pref));
    }

    // GET /api/notifications/push/public-key — the VAPID key the client subscribes with.
    // 404 when unconfigured so the client can hide/disable the push toggle honestly.
    [HttpGet("push/public-key")]
    public IActionResult GetPushPublicKey() =>
        string.IsNullOrEmpty(_webPush.PublicKey)
            ? NotFound()
            : Ok(new PushPublicKeyResponse(_webPush.PublicKey));

    // PUT /api/notifications/push-subscription — upsert keyed by Endpoint. An endpoint is
    // device+origin-scoped, so a row already registered by a DIFFERENT user (same browser,
    // new login) is reassigned to the caller rather than duplicated; re-subscribing just
    // refreshes the encryption keys.
    [HttpPut("push-subscription")]
    public async Task<IActionResult> SavePushSubscription(
        [FromBody] SavePushSubscriptionRequest request
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var existing = await _pushSubscriptions.GetByEndpointAsync(request.Endpoint);
        if (existing is not null)
        {
            existing.UserId = userId.Value;
            existing.P256dh = request.P256dh;
            existing.AuthKey = request.AuthKey;
        }
        else
        {
            await _pushSubscriptions.AddAsync(
                new UserPushSubscription
                {
                    Id = _snowflake.NextId(),
                    UserId = userId.Value,
                    Endpoint = request.Endpoint,
                    P256dh = request.P256dh,
                    AuthKey = request.AuthKey,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }

        await _pushSubscriptions.SaveChangesAsync();
        return NoContent();
    }

    // DELETE /api/notifications/push-subscription?endpoint= — idempotent; only the owner's
    // own row is removed (someone else's endpoint is left alone and still returns 204,
    // so the response never leaks whether a foreign endpoint exists).
    [HttpDelete("push-subscription")]
    public async Task<IActionResult> DeletePushSubscription([FromQuery] string endpoint)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();
        if (string.IsNullOrEmpty(endpoint))
            return BadRequest();

        var existing = await _pushSubscriptions.GetByEndpointAsync(endpoint);
        if (existing is not null && existing.UserId == userId.Value)
        {
            _pushSubscriptions.Remove(existing);
            await _pushSubscriptions.SaveChangesAsync();
        }
        return NoContent();
    }

    // A null row means "every preference at its default (enabled)".
    private static NotificationPreferenceResponse ToResponse(NotificationPreference? p) =>
        p is null
            ? new NotificationPreferenceResponse(true, true, true, true, true)
            : new NotificationPreferenceResponse(
                p.MentionsEnabled,
                p.RepliesEnabled,
                p.FriendRequests,
                p.GuildInvites,
                p.PushEnabled
            );

    private long? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
