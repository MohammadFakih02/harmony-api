using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
[EnableRateLimiting("api")]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IGuildRepository _guilds;
    private readonly UserManager<User> _userManager;
    private readonly IPresenceService _presence;
    private readonly IUserBlockRepository _blocks;
    private readonly IFriendRepository _friends;
    private readonly IHubBroadcaster _broadcaster;

    public UsersController(
        IUserRepository users,
        IGuildRepository guilds,
        UserManager<User> userManager,
        IPresenceService presence,
        IUserBlockRepository blocks,
        IFriendRepository friends,
        IHubBroadcaster broadcaster
    )
    {
        _users = users;
        _guilds = guilds;
        _userManager = userManager;
        _presence = presence;
        _blocks = blocks;
        _friends = friends;
        _broadcaster = broadcaster;
    }

    // GET /api/users/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        return Ok(ToProfileResponse(user));
    }

    // PATCH /api/users/me
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserRequest request)
    {
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        if (request.Username is not null)
        {
            // Check username isn't already taken by someone else
            var existing = await _userManager.FindByNameAsync(request.Username);
            if (existing is not null && existing.Id != user.Id)
                return Conflict(new { error = "Username already taken." });

            user.UserName = request.Username;
            // Keep normalized username in sync so Identity lookups still work
            user.NormalizedUserName = request.Username.ToUpperInvariant();
        }

        if (request.Bio is not null)
            user.Bio = request.Bio;
        if (request.StatusMessage is not null)
            user.StatusMessage = request.StatusMessage;

        await _users.SaveChangesAsync();

        return Ok(ToProfileResponse(user));
    }

    // PATCH /api/users/me/status — durable preferred status (online|away|dnd|invisible)
    [HttpPatch("me/status")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request)
    {
        // Shape (the four allowed values) is enforced by UpdateStatusRequestValidator.
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        user.PreferredStatus = request.Status;
        // Expiry only applies to a non-default status — "online" is already the revert
        // target, so an expiry on it is meaningless and is cleared.
        user.PreferredStatusExpiresAt =
            request.Status != PresenceStatus.Online && request.ExpiresInMinutes is > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    + (long)request.ExpiresInMinutes.Value * 60_000
                : null;
        await _users.SaveChangesAsync(); // Postgres is the source of truth

        // Update the Redis cache + recompute the effective status + broadcast StatusChanged.
        await _presence.SetPreferredStatusAsync(user.Id, request.Status);

        return Ok(ToProfileResponse(user));
    }

    // PATCH /api/users/me/custom-status — the free-text custom status message + clear-after.
    [HttpPatch("me/custom-status")]
    public async Task<IActionResult> UpdateCustomStatus(
        [FromBody] UpdateCustomStatusRequest request
    )
    {
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        user.StatusMessage = message;
        // No message ⇒ no expiry; otherwise honour the optional clear-after.
        user.StatusMessageExpiresAt =
            message is not null && request.ExpiresInMinutes is > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    + (long)request.ExpiresInMinutes.Value * 60_000
                : null;
        await _users.SaveChangesAsync();

        // Live half: cache the message + broadcast StatusChanged to friends/own tabs.
        await _presence.SetCustomStatusAsync(user.Id, message);

        return Ok(ToProfileResponse(user));
    }

    // GET /api/users/presence?ids=1,2,3 — effective status + custom message for the member list
    [HttpGet("presence")]
    public async Task<IActionResult> GetPresence([FromQuery] string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return Ok(new Dictionary<string, UserPresenceResponse>());

        var userIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        var statuses = await _presence.GetStatusesAsync(userIds);
        var messages = await _presence.GetStatusMessagesAsync(userIds);

        // Serialize ids as strings to match the Snowflake-as-string convention everywhere else.
        // A user who appears offline never exposes their custom message.
        return Ok(
            statuses.ToDictionary(
                kv => kv.Key.ToString(),
                kv => new UserPresenceResponse(
                    kv.Value,
                    kv.Value == "offline" ? null : messages.GetValueOrDefault(kv.Key)
                )
            )
        );
    }

    // GET /api/users/{id}
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var user = await _users.GetByIdAsync(id);
        if (user is null)
            return NotFound();

        return Ok(ToPublicResponse(user));
    }

    // GET /api/users/me/guilds
    [HttpGet("me/guilds")]
    public async Task<IActionResult> GetMyGuilds()
    {
        var guilds = await _guilds.GetByUserIdAsync(GetUserId());
        return Ok(
            guilds.Select(g => new GuildResponse(
                g.Id,
                g.Name,
                g.Description,
                g.OwnerId,
                g.IconKey,
                g.BannerKey,
                g.IsPublic,
                g.InviteCode,
                g.MemberCount,
                g.CreatedAt
            ))
        );
    }

    // -------------------------------------------------------------------------
    // Blocking — block CRUD + a bidirectional query seam. The effects
    // (DM/mention/presence suppression) belong to Phase 4 features that will
    // consume IUserBlockRepository.AreBlockedAsync; nothing consumes it yet.
    // -------------------------------------------------------------------------

    // POST /api/users/{id}/block — idempotent
    [HttpPost("{id:long}/block")]
    public async Task<IActionResult> Block(long id)
    {
        var me = GetUserId();
        if (id == me)
            return BadRequest(new { error = "You cannot block yourself." });

        var target = await _users.GetByIdAsync(id);
        if (target is null)
            return NotFound();

        var existing = await _blocks.GetAsync(me, id);
        if (existing is null)
        {
            await _blocks.AddAsync(
                new UserBlock
                {
                    BlockerId = me,
                    BlockedId = id,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
            await _blocks.SaveChangesAsync();
        }

        // Blocking severs any friendship or pending request between the two users
        // (the deferred §5.14 cleanup, now that friends-system exists).
        var friendship = await _friends.GetBetweenAsync(me, id);
        if (friendship is not null)
        {
            _friends.Remove(friendship);
            await _friends.SaveChangesAsync();

            // Notify the blocked user to prune; keep my own other tabs in sync too.
            await _broadcaster.BroadcastFriendRemovedAsync(id, new FriendRemovedPayload(me));
            await _broadcaster.BroadcastFriendRemovedAsync(me, new FriendRemovedPayload(id));
        }

        return NoContent();
    }

    // DELETE /api/users/{id}/block — idempotent
    [HttpDelete("{id:long}/block")]
    public async Task<IActionResult> Unblock(long id)
    {
        var me = GetUserId();
        var existing = await _blocks.GetAsync(me, id);
        if (existing is not null)
        {
            _blocks.Remove(existing);
            await _blocks.SaveChangesAsync();
        }

        return NoContent();
    }

    // GET /api/users/me/blocks
    [HttpGet("me/blocks")]
    public async Task<IActionResult> GetMyBlocks()
    {
        var blocks = await _blocks.GetByBlockerAsync(GetUserId());
        if (blocks.Count == 0)
            return Ok(Array.Empty<BlockResponse>());

        var users = await _users.GetByIdsAsync(blocks.Select(b => b.BlockedId));
        var result = blocks
            .Where(b => users.ContainsKey(b.BlockedId))
            .Select(b =>
            {
                var u = users[b.BlockedId];
                return new BlockResponse(
                    u.Id,
                    u.UserName!,
                    u.AvatarKey,
                    u.BannerKey,
                    b.CreatedAt
                );
            });

        return Ok(result);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static UserProfileResponse ToProfileResponse(User u) =>
        new(
            u.Id,
            u.UserName!,
            u.Email!,
            u.AvatarKey,
            u.BannerKey,
            u.Bio,
            u.StatusMessage,
            u.StatusMessageExpiresAt,
            u.PreferredStatus,
            u.PreferredStatusExpiresAt,
            u.AccountStatus,
            u.CreatedAt
        );

    private static PublicUserResponse ToPublicResponse(User u) =>
        new(u.Id, u.UserName!, u.AvatarKey, u.BannerKey, u.Bio, u.StatusMessage);
}
