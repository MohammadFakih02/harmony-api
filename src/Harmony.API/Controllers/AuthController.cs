using Harmony.Application.DTOs.Requests;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("api")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IWebHostEnvironment _environment;

    public AuthController(IAuthService authService, IWebHostEnvironment environment)
    {
        _authService = authService;
        _environment = environment;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var (response, rawRefreshToken) = await _authService.RegisterAsync(request);
        SetRefreshCookie(rawRefreshToken);
        return Ok(response);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var (response, rawRefreshToken) = await _authService.LoginAsync(request);
        SetRefreshCookie(rawRefreshToken);
        return Ok(response);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Refresh()
    {
        var rawToken = Request.Cookies["refresh_token"];
        if (string.IsNullOrEmpty(rawToken))
            return Unauthorized(new { error = "No refresh token." });

        var (response, rawRefreshToken) = await _authService.RefreshAsync(rawToken);

        // Write the cookie only if a new refresh token was actually generated
        // (Grace-period requests bypass this to preserve the active cookie)
        if (!string.IsNullOrEmpty(rawRefreshToken))
        {
            SetRefreshCookie(rawRefreshToken);
        }

        return Ok(response);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var rawToken = Request.Cookies["refresh_token"];
        if (!string.IsNullOrEmpty(rawToken))
            await _authService.LogoutAsync(rawToken);

        DeleteRefreshCookie();
        return NoContent();
    }

    private void SetRefreshCookie(string rawToken)
    {
        Response.Cookies.Append(
            "refresh_token",
            rawToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = _environment.IsProduction(),
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
                Secure = _environment.IsProduction(),
                SameSite = SameSiteMode.Strict,
            }
        );
    }
}
