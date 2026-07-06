using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
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
/// Friend relationships: request, accept, list, and remove. A friendship is a single
/// Friends row (requester, addressee) with status pending → accepted. Decline / cancel /
/// unfriend all delete the row. Real-time FriendRequest / FriendAccepted / FriendRemoved
/// events are pushed per-user via Clients.User; the persisted Notification row + offline
/// push belong to the notifications-system feature (deferred).
/// </summary>
[ApiController]
[Route("api/friends")]
[Authorize]
[EnableRateLimiting("api")]
public class FriendsController : ControllerBase
{
    private const string StatusPending = "pending";
    private const string StatusAccepted = "accepted";

    private readonly IFriendRepository _friends;
    private readonly IUserRepository _users;
    private readonly IIdentityService _identity;
    private readonly IUserBlockRepository _blocks;
    private readonly IHubBroadcaster _broadcaster;
    private readonly INotificationService _notifications;
    private readonly ILogger<FriendsController> _logger;

    public FriendsController(
        IFriendRepository friends,
        IUserRepository users,
        IIdentityService identity,
        IUserBlockRepository blocks,
        IHubBroadcaster broadcaster,
        INotificationService notifications,
        ILogger<FriendsController> logger
    )
    {
        _friends = friends;
        _users = users;
        _identity = identity;
        _blocks = blocks;
        _broadcaster = broadcaster;
        _notifications = notifications;
        _logger = logger;
    }

    // GET /api/friends — accepted friends
    [HttpGet]
    public async Task<IActionResult> GetFriends()
    {
        var me = GetUserId();
        var rows = await _friends.GetAcceptedAsync(me);
        if (rows.Count == 0)
            return Ok(Array.Empty<FriendResponse>());

        var users = await _users.GetByIdsAsync(rows.Select(r => Other(r, me)));
        var result = rows
            .Where(r => users.ContainsKey(Other(r, me)))
            .Select(r =>
            {
                var u = users[Other(r, me)];
                return new FriendResponse(
                    u.Id,
                    u.UserName!,
                    u.AvatarKey,
                    u.BannerKey,
                    r.UpdatedAt
                );
            });

        return Ok(result);
    }

    // GET /api/friends/pending — incoming + outgoing pending requests
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending()
    {
        var me = GetUserId();
        var rows = await _friends.GetPendingForAsync(me);
        if (rows.Count == 0)
            return Ok(Array.Empty<PendingFriendResponse>());

        var users = await _users.GetByIdsAsync(rows.Select(r => Other(r, me)));
        var result = rows
            .Where(r => users.ContainsKey(Other(r, me)))
            .Select(r =>
            {
                var u = users[Other(r, me)];
                return new PendingFriendResponse(
                    u.Id,
                    u.UserName!,
                    u.AvatarKey,
                    u.BannerKey,
                    r.RequesterId == me ? "outgoing" : "incoming",
                    r.CreatedAt
                );
            });

        return Ok(result);
    }

    // POST /api/friends/request — send a request by username
    [HttpPost("request")]
    public async Task<IActionResult> SendRequest([FromBody] SendFriendRequestRequest request)
    {
        // Shape (non-empty username) is enforced by SendFriendRequestRequestValidator.
        var me = GetUserId();

        var target = await _identity.FindByNameAsync(request.Username);
        if (target is null)
            return NotFound(new { error = "No user with that username." });

        if (target.Id == me)
            return BadRequest(new { error = "You cannot friend yourself." });

        // Blocking suppresses friend requests in either direction.
        if (await _blocks.AreBlockedAsync(me, target.Id))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Unable to send a request to this user." });

        var existing = await _friends.GetBetweenAsync(me, target.Id);
        if (existing is not null)
        {
            if (existing.Status == StatusAccepted)
                return Conflict(new { error = "You are already friends." });

            // Pending and they already requested me → accept it (Discord behavior).
            if (existing.AddresseeId == me)
                return await AcceptRow(existing, me);

            return Conflict(new { error = "A request is already pending." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _friends.AddAsync(
            new Friend
            {
                RequesterId = me,
                AddresseeId = target.Id,
                Status = StatusPending,
                CreatedAt = now,
                UpdatedAt = now,
            }
        );
        await _friends.SaveChangesAsync();

        // Push the incoming request to the addressee with the requester's identity.
        var meUser = await _users.GetByIdAsync(me);
        if (meUser is not null)
            await _broadcaster.BroadcastFriendRequestAsync(target.Id, ToPayload(meUser));

        // Best-effort: the friend request itself already persisted + broadcast above,
        // so a notification-side failure (preference/mute/block lookup, persist) must
        // not turn a successful request into a 500.
        try
        {
            await _notifications.CreateFriendRequestNotificationAsync(target.Id, me);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "FriendRequest: notification creation failed for AddresseeId {AddresseeId} — request persisted, continuing",
                target.Id
            );
        }

        return Ok(
            new PendingFriendResponse(
                target.Id,
                target.UserName!,
                target.AvatarKey,
                target.BannerKey,
                "outgoing",
                now
            )
        );
    }

    // PATCH /api/friends/{requesterId}/accept — accept an incoming request
    [HttpPatch("{requesterId:long}/accept")]
    public async Task<IActionResult> Accept(long requesterId)
    {
        var me = GetUserId();
        var row = await _friends.GetBetweenAsync(me, requesterId);

        // Only the addressee of a still-pending request can accept it.
        if (row is null || row.Status != StatusPending || row.AddresseeId != me)
            return NotFound(new { error = "No pending request from this user." });

        return await AcceptRow(row, me);
    }

    // DELETE /api/friends/{userId} — decline / cancel / unfriend (idempotent)
    [HttpDelete("{userId:long}")]
    public async Task<IActionResult> Remove(long userId)
    {
        var me = GetUserId();
        var row = await _friends.GetBetweenAsync(me, userId);
        if (row is not null)
        {
            _friends.Remove(row);
            await _friends.SaveChangesAsync();

            // Tell the other party to prune, and keep my own other tabs in sync.
            await _broadcaster.BroadcastFriendRemovedAsync(userId, new FriendRemovedPayload(me));
            await _broadcaster.BroadcastFriendRemovedAsync(me, new FriendRemovedPayload(userId));
        }

        return NoContent();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<IActionResult> AcceptRow(Friend row, long me)
    {
        row.Status = StatusAccepted;
        row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _friends.SaveChangesAsync();

        var otherId = Other(row, me);
        var users = await _users.GetByIdsAsync(new[] { me, otherId });

        // Notify both parties, each with the other user's identity.
        if (users.TryGetValue(me, out var meUser))
            await _broadcaster.BroadcastFriendAcceptedAsync(otherId, ToPayload(meUser));
        if (users.TryGetValue(otherId, out var otherUser))
            await _broadcaster.BroadcastFriendAcceptedAsync(me, ToPayload(otherUser));

        if (users.TryGetValue(otherId, out var friend))
            return Ok(
                new FriendResponse(
                    friend.Id,
                    friend.UserName!,
                    friend.AvatarKey,
                    friend.BannerKey,
                    row.UpdatedAt
                )
            );

        return NoContent();
    }

    // Internal so BootstrapController can reuse it when composing the boot payload.
    internal static long Other(Friend f, long me) => f.RequesterId == me ? f.AddresseeId : f.RequesterId;

    private static FriendUserPayload ToPayload(User u) =>
        new(u.Id, u.UserName!, u.AvatarKey, u.BannerKey);

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
