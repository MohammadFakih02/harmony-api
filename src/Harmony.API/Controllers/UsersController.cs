using System.Security.Claims;
using Harmony.Core.DTOs.Requests;
using Harmony.Core.DTOs.Responses;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
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

    public UsersController(
        IUserRepository users,
        IGuildRepository guilds,
        UserManager<User> userManager
    )
    {
        _users = users;
        _guilds = guilds;
        _userManager = userManager;
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
            u.AccountStatus,
            u.CreatedAt
        );

    private static PublicUserResponse ToPublicResponse(User u) =>
        new(u.Id, u.UserName!, u.Discriminator, u.AvatarKey, u.BannerKey, u.Bio, u.StatusMessage);
}
