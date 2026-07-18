using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
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
        var (response, rawRefreshToken) = await _authService.LoginAsync(
            request,
            Request.Cookies["trusted_device"]
        );
        // No refresh cookie yet when a 2FA challenge is required — the caller isn't authenticated
        // until POST /api/auth/2fa/verify succeeds.
        if (rawRefreshToken is not null)
            SetRefreshCookie(rawRefreshToken);
        return Ok(response);
    }

    // POST /api/auth/2fa/verify — anonymous: completes a login challenge with the emailed code.
    // Sets the refresh cookie (now fully authenticated) and, if requested, a 30-day "trusted_device"
    // cookie so future logins from this browser skip the challenge.
    [HttpPost("2fa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Verify2fa([FromBody] Verify2faRequest request)
    {
        var (response, rawRefreshToken, trustedDeviceToken) = await _authService.Verify2faAsync(request);
        SetRefreshCookie(rawRefreshToken);
        if (trustedDeviceToken is not null)
            SetTrustedDeviceCookie(trustedDeviceToken);
        return Ok(response);
    }

    // POST /api/auth/2fa/resend — anonymous: the challenge screen isn't authenticated yet, only
    // holds the opaque challenge token. Same "204 unless the send genuinely failed" contract as
    // verify-email/request (see the comment there).
    [HttpPost("2fa/resend")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Resend2fa([FromBody] Resend2faRequest request)
    {
        var sent = await _authService.Resend2faAsync(request.ChallengeToken);
        if (!sent)
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "Could not send the code — try again in a moment." }
            );
        return NoContent();
    }

    // POST /api/auth/2fa/enable/request — starts the enable-2FA flow: verifies the password,
    // requires a verified email, emails a setup code.
    [HttpPost("2fa/enable/request")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Enable2faRequest([FromBody] Enable2faRequest request)
    {
        var sent = await _authService.Enable2faRequestAsync(GetUserId(), request.Password);
        if (!sent)
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "Could not send the code — try again in a moment." }
            );
        return NoContent();
    }

    // POST /api/auth/2fa/enable/confirm — confirms the emailed setup code and turns 2FA on.
    [HttpPost("2fa/enable/confirm")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Enable2faConfirm([FromBody] Confirm2faRequest request)
    {
        await _authService.Enable2faConfirmAsync(GetUserId(), request.Code);
        return NoContent();
    }

    // POST /api/auth/2fa/disable — verifies the password, turns 2FA off, and revokes every
    // trusted device (a stale "remembered" cookie must not survive 2FA being turned back on later).
    [HttpPost("2fa/disable")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Disable2fa([FromBody] Disable2faRequest request)
    {
        await _authService.Disable2faAsync(GetUserId(), request.Password);
        DeleteTrustedDeviceCookie();
        return NoContent();
    }

    // DELETE /api/auth/2fa/trusted-devices — "Require 2FA on all devices again": revokes every
    // trusted device without touching whether 2FA itself is enabled.
    [HttpDelete("2fa/trusted-devices")]
    [Authorize]
    public async Task<IActionResult> ClearTrustedDevices()
    {
        await _authService.ClearTrustedDevicesAsync(GetUserId());
        DeleteTrustedDeviceCookie();
        return NoContent();
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

    // AllowAnonymous (like Refresh): logout is driven entirely by the refresh_token
    // cookie, not the bearer token. If this required [Authorize], an expired 15-min
    // access token would 401 before Logout ran — leaving the refresh token unrevoked
    // and the cookie intact, so a later silent refresh (e.g. a push-notification click)
    // would log the "logged-out" user back in with no credentials.
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        var rawToken = Request.Cookies["refresh_token"];
        if (!string.IsNullOrEmpty(rawToken))
            await _authService.LogoutAsync(rawToken);

        DeleteRefreshCookie();
        return NoContent();
    }

    // POST /api/auth/verify-email/request — (re)send the verification email to the caller's own
    // address. 204 covers both a fresh send and a no-op (already confirmed / inside the cooldown) —
    // those aren't distinguishable from the client's point of view. A genuine SMTP failure is NOT
    // a no-op though: surface it as an error so the frontend doesn't tell the user "sent" for an
    // email that never left the building (and doesn't lock them out of retrying via the cooldown —
    // the service releases it on a failed send).
    [HttpPost("verify-email/request")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> RequestEmailVerification()
    {
        var sent = await _authService.RequestEmailVerificationAsync(GetUserId());
        if (!sent)
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "Could not send the verification email — try again in a moment." }
            );
        return NoContent();
    }

    // POST /api/auth/verify-email/confirm — anonymous: the link is followed from an email client
    // that may not carry the session, so this can't require the caller to already be logged in.
    [HttpPost("verify-email/confirm")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request)
    {
        var confirmed = await _authService.ConfirmEmailAsync(request.UserId, request.Token);
        return confirmed ? NoContent() : BadRequest(new { error = "Invalid or expired link." });
    }

    // POST /api/auth/forgot-password — anonymous, always 204. Never reveals whether the email
    // belongs to an account: an unknown email, a cooldown, and a genuine SMTP failure are all
    // indistinguishable from the caller's point of view (unlike verify-email/2fa resends, which
    // ARE authenticated and so can safely surface a real send failure).
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        await _authService.ForgotPasswordAsync(request.Email);
        return NoContent();
    }

    // POST /api/auth/reset-password — anonymous: revokes every refresh token + trusted device for
    // the user on success, so every other logged-in session (and any "remembered" 2FA device) dies
    // immediately.
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var succeeded = await _authService.ResetPasswordAsync(
            request.UserId,
            request.Token,
            request.NewPassword
        );
        return succeeded
            ? NoContent()
            : BadRequest(new { error = "Invalid or expired link." });
    }

    // POST /api/auth/google — anonymous: signs in (auto-registering or auto-linking by verified
    // email as needed) from a Google Identity Services ID token. Never a 2FA challenge — a
    // federated Google sign-in bypasses local email-code 2FA.
    [HttpPost("google")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request)
    {
        var (response, rawRefreshToken) = await _authService.GoogleLoginAsync(request.IdToken);
        SetRefreshCookie(rawRefreshToken);
        return Ok(response);
    }

    // POST /api/auth/change-password — verifies the current password, then (for a 2FA-enabled
    // account, on the first call) emails a step-up code and returns requiresCode without changing
    // anything. Once past that gate, changes the password, revokes every other session (refresh
    // tokens + trusted devices), and re-issues fresh tokens so the caller stays signed in.
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var (response, rawRefreshToken) = await _authService.ChangePasswordAsync(
            GetUserId(),
            request.CurrentPassword,
            request.NewPassword,
            request.Code
        );
        if (rawRefreshToken is not null)
        {
            SetRefreshCookie(rawRefreshToken);
            DeleteTrustedDeviceCookie();
        }
        return Ok(response);
    }

    // POST /api/auth/set-password — adds a local password to a passwordless (Google-only)
    // account. No current-password field: the authenticated session is the proof of ownership.
    [HttpPost("set-password")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request)
    {
        await _authService.SetPasswordAsync(GetUserId(), request.NewPassword);
        return NoContent();
    }

    // POST /api/auth/change-email/request — verifies the password, then (for a 2FA-enabled
    // account, on the first call) emails a step-up code and returns requiresCode without sending
    // the actual change-email link yet. Once past that gate, emails the confirmation link to the
    // NEW address; the old email stays active until that link is followed.
    [HttpPost("change-email/request")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ChangeEmailRequest([FromBody] ChangeEmailRequest request)
    {
        var requiresCode = await _authService.ChangeEmailRequestAsync(
            GetUserId(),
            request.Password,
            request.NewEmail,
            request.Code
        );
        return Ok(new ChangeEmailRequestResponse(requiresCode));
    }

    // POST /api/auth/change-email/confirm — anonymous: the link is followed from an email client
    // that may not carry the session, same reasoning as verify-email/confirm.
    [HttpPost("change-email/confirm")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ConfirmEmailChange([FromBody] ConfirmEmailChangeRequest request)
    {
        var confirmed = await _authService.ConfirmEmailChangeAsync(
            request.UserId,
            request.Email,
            request.Token
        );
        return confirmed ? NoContent() : BadRequest(new { error = "Invalid or expired link." });
    }

    // POST /api/auth/change-username — verifies the password, renames the user, and live-broadcasts
    // the new name to guilds/friends/own-tabs.
    [HttpPost("change-username")]
    [Authorize]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ChangeUsername([FromBody] ChangeUsernameRequest request)
    {
        await _authService.ChangeUsernameAsync(GetUserId(), request.Password, request.NewUsername);
        return NoContent();
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

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

    private void SetTrustedDeviceCookie(string rawToken)
    {
        Response.Cookies.Append(
            "trusted_device",
            rawToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = _environment.IsProduction(),
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
            }
        );
    }

    private void DeleteTrustedDeviceCookie()
    {
        Response.Cookies.Delete(
            "trusted_device",
            new CookieOptions
            {
                HttpOnly = true,
                Secure = _environment.IsProduction(),
                SameSite = SameSiteMode.Strict,
            }
        );
    }
}
