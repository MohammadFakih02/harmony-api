using System.Security.Cryptography;
using System.Text;
using Harmony.API.DTOs.Requests;
using Harmony.API.DTOs.Responses;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Services;
using Harmony.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("api")] // general limiter on all auth endpoints
public class AuthController : ControllerBase
{
    private readonly UserManager<User> _userManager;
    private readonly HarmonyDbContext _db;
    private readonly IJwtService _jwtService;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IConfiguration _config;

    public AuthController(
        UserManager<User> userManager,
        HarmonyDbContext db,
        IJwtService jwtService,
        ISnowflakeIdGenerator snowflake,
        IConfiguration config
    )
    {
        _userManager = userManager;
        _db = db;
        _jwtService = jwtService;
        _snowflake = snowflake;
        _config = config;
    }

    // POST /api/auth/register
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("login")] // stricter limiter overrides "api" for this endpoint
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (await _userManager.FindByEmailAsync(request.Email) is not null)
            return Conflict(new { error = "Email already in use." });

        if (await _userManager.FindByNameAsync(request.Username) is not null)
            return Conflict(new { error = "Username already taken." });

        var user = new User
        {
            Id = _snowflake.NextId(),
            UserName = request.Username,
            Email = request.Email,
            Discriminator = GenerateDiscriminator(),
            AccountStatus = "active",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        var (accessToken, refreshToken) = await IssueTokens(user);
        SetRefreshCookie(refreshToken);

        return Ok(new AuthResponse(accessToken, ToUserResponse(user)));
    }

    // POST /api/auth/login
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")] // stricter limiter — 10 attempts/min by IP
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
            return Unauthorized(new { error = "Invalid email or password." });

        if (user.AccountStatus != "active")
            return Forbid();

        var (accessToken, refreshToken) = await IssueTokens(user);
        SetRefreshCookie(refreshToken);

        return Ok(new AuthResponse(accessToken, ToUserResponse(user)));
    }

    // POST /api/auth/refresh
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("login")] // treat refresh like login — abuse vector
    public async Task<IActionResult> Refresh()
    {
        var rawToken = Request.Cookies["refresh_token"];
        if (string.IsNullOrEmpty(rawToken))
            return Unauthorized(new { error = "No refresh token." });

        var tokenHash = _jwtService.HashRefreshToken(rawToken);

        var stored = await _db
            .RefreshTokens.Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash);

        if (stored is null)
            return Unauthorized(new { error = "Invalid refresh token." });

        // Detect reuse of a revoked token — revoke entire family
        if (stored.RevokedAt is not null)
        {
            await RevokeFamily(stored.FamilyId);
            DeleteRefreshCookie();
            return Unauthorized(
                new { error = "Refresh token reuse detected. Please log in again." }
            );
        }

        if (stored.ExpiresAt < DateTimeOffset.UtcNow)
        {
            stored.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
            DeleteRefreshCookie();
            return Unauthorized(new { error = "Refresh token expired." });
        }

        // Rotate: revoke old, issue new in same family
        stored.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        var (accessToken, newRefreshToken) = await IssueTokens(stored.User, stored.FamilyId);
        SetRefreshCookie(newRefreshToken);

        return Ok(new AuthResponse(accessToken, ToUserResponse(stored.User)));
    }

    // POST /api/auth/logout
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var rawToken = Request.Cookies["refresh_token"];
        if (!string.IsNullOrEmpty(rawToken))
        {
            var tokenHash = _jwtService.HashRefreshToken(rawToken);
            var stored = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == tokenHash);
            if (stored is not null && stored.RevokedAt is null)
            {
                stored.RevokedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync();
            }
        }

        DeleteRefreshCookie();
        return NoContent();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<(string accessToken, string rawRefreshToken)> IssueTokens(
        User user,
        Guid? familyId = null
    )
    {
        var accessToken = _jwtService.GenerateAccessToken(user);
        var rawRefresh = _jwtService.GenerateRefreshToken();
        var refreshExpiry = int.Parse(_config["Jwt:RefreshTokenExpiryDays"] ?? "7");

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = _jwtService.HashRefreshToken(rawRefresh),
            FamilyId = familyId ?? Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshExpiry),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _db.RefreshTokens.Add(refreshToken);
        await _db.SaveChangesAsync();

        return (accessToken, rawRefresh);
    }

    private async Task RevokeFamily(Guid familyId)
    {
        var tokens = await _db
            .RefreshTokens.Where(r => r.FamilyId == familyId && r.RevokedAt == null)
            .ToListAsync();

        foreach (var t in tokens)
            t.RevokedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
    }

    private void SetRefreshCookie(string rawToken)
    {
        Response.Cookies.Append(
            "refresh_token",
            rawToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddDays(7),
            }
        );
    }

    private void DeleteRefreshCookie()
    {
        Response.Cookies.Delete(
            "refresh_token",
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
            }
        );
    }

    private static string GenerateDiscriminator() => Random.Shared.Next(0, 10000).ToString("D4");

    private static UserResponse ToUserResponse(User user) =>
        new(
            user.Id,
            user.UserName!,
            user.Discriminator,
            user.Email!,
            user.AvatarKey,
            user.AccountStatus
        );
}
