using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
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

    public UsersController(
        IUserRepository users,
        IGuildRepository guilds,
        UserManager<User> userManager,
        IPresenceService presence
    )
    {
        _users = users;
        _guilds = guilds;
        _userManager = userManager;
        _presence = presence;
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
        await _users.SaveChangesAsync(); // Postgres is the source of truth

        // Update the Redis cache + recompute the effective status + broadcast StatusChanged.
        await _presence.SetPreferredStatusAsync(user.Id, request.Status);

        return Ok(ToProfileResponse(user));
    }

    // GET /api/users/presence?ids=1,2,3 — public effective statuses for the member-list dots
    [HttpGet("presence")]
    public async Task<IActionResult> GetPresence([FromQuery] string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return Ok(new Dictionary<string, string>());

        var userIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        var statuses = await _presence.GetStatusesAsync(userIds);

        // Serialize ids as strings to match the Snowflake-as-string convention everywhere else.
        return Ok(statuses.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));
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
    // Helpers
    // -------------------------------------------------------------------------

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static UserProfileResponse ToProfileResponse(User u) =>
        new(
            u.Id,
            u.UserName!,
            u.Discriminator,
            u.Email!,
            u.AvatarKey,
            u.BannerKey,
            u.Bio,
            u.StatusMessage,
            u.PreferredStatus,
            u.AccountStatus,
            u.CreatedAt
        );

    private static PublicUserResponse ToPublicResponse(User u) =>
        new(u.Id, u.UserName!, u.Discriminator, u.AvatarKey, u.BannerKey, u.Bio, u.StatusMessage);
}
