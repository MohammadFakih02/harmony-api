using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Registration, login, token refresh, 2FA, OAuth, and the credential-change flows.
/// </summary>
/// <remarks>
/// Authentication is split across two tokens. A short-lived (15-minute) JWT access token is returned
/// in the response body and sent as a bearer header on subsequent calls. A long-lived (7-day) refresh
/// token lives only in an <c>HttpOnly</c>, <c>SameSite=Strict</c> cookie the browser cannot read, and
/// is exchanged for a fresh access token at <c>POST /api/auth/refresh</c>. This is why several
/// endpoints here are <c>AllowAnonymous</c> yet still act on a specific user — they authenticate off
/// the refresh cookie, not the (possibly expired) access token.
/// </remarks>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting("api")]
public class AuthController : HarmonyControllerBase
{
    private readonly IAuthService _authService;
    private readonly IWebHostEnvironment _environment;

    public AuthController(IAuthService authService, IWebHostEnvironment environment)
    {
        _authService = authService;
        _environment = environment;
    }

    /// <summary>
    /// Creates a new account and signs it in immediately: returns an access token and sets the refresh
    /// cookie. The new account starts with an unverified email.
    /// </summary>
    /// <response code="200">Registered. Body carries the access token and the user profile.</response>
    /// <response code="400">Validation failed, or the username/email is already taken.</response>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var (response, rawRefreshToken) = await _authService.RegisterAsync(request);
        SetRefreshCookie(rawRefreshToken);
        return Ok(response);
    }

    /// <summary>
    /// Signs in with an identifier (username or email) and password. If the account has 2FA enabled
    /// and this browser isn't a remembered trusted device, no session is issued yet — the response
    /// carries <c>twoFactorRequired: true</c> and an opaque challenge token, and the caller must
    /// complete <c>POST /api/auth/2fa/verify</c>.
    /// </summary>
    /// <remarks>
    /// The <c>trusted_device</c> cookie, if present, is what lets a 2FA-enabled account skip the
    /// challenge. The refresh cookie is set only when the login fully succeeds — a 2FA challenge
    /// leaves the caller unauthenticated until verify.
    /// </remarks>
    /// <response code="200">Either a full session (access token + refresh cookie) or a 2FA challenge.</response>
    /// <response code="401">Unknown identifier or wrong password.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

    /// <summary>
    /// Completes a 2FA login challenge with the emailed code. Sets the refresh cookie (the caller is
    /// now fully authenticated) and, if the challenge asked to remember this device, a 30-day
    /// <c>trusted_device</c> cookie so future logins from this browser skip the challenge.
    /// </summary>
    /// <response code="200">Verified. Access token in the body, refresh cookie set.</response>
    /// <response code="401">Invalid, expired, or too-many-attempts code.</response>
    [HttpPost("2fa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

    /// <summary>
    /// Exchanges the <c>refresh_token</c> cookie for a fresh access token, rotating the refresh
    /// token in the process. This is the endpoint the client calls silently when its 15-minute
    /// access token expires.
    /// </summary>
    /// <remarks>
    /// Anonymous by design: it authenticates off the refresh cookie, not the (likely expired) bearer
    /// token. A short grace window lets a just-rotated token still refresh once, so two near-
    /// simultaneous refreshes don't log the user out — those requests preserve the existing cookie.
    /// </remarks>
    /// <response code="200">New access token issued.</response>
    /// <response code="401">No refresh cookie, or the token is revoked/expired.</response>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

    /// <summary>
    /// Revokes the refresh token and clears its cookie.
    /// </summary>
    /// <remarks>
    /// <c>AllowAnonymous</c> (like <c>refresh</c>) is deliberate and load-bearing: logout is driven
    /// entirely by the refresh cookie, not the bearer token. If this required <c>[Authorize]</c>, an
    /// expired 15-minute access token would 401 before logout ran — leaving the refresh token
    /// unrevoked and the cookie intact, so a later silent refresh (e.g. a push-notification click)
    /// would log the "logged-out" user back in with no credentials.
    /// </remarks>
    /// <response code="204">Logged out (also returned when there was no session to revoke).</response>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
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

    /// <summary>
    /// Sends a password-reset link to the given email — if it belongs to an account.
    /// </summary>
    /// <remarks>
    /// Always 204, and deliberately so: it never reveals whether the email belongs to an account. An
    /// unknown email, an active cooldown, and a genuine SMTP failure are all indistinguishable to the
    /// caller — that's what prevents this endpoint from being an account-enumeration oracle. (Unlike
    /// the verify-email and 2FA resends, which ARE authenticated and so can safely surface a real
    /// send failure.)
    /// </remarks>
    /// <response code="204">Always — regardless of whether an email was actually sent.</response>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        await _authService.ForgotPasswordAsync(request.Email);
        return NoContent();
    }

    /// <summary>
    /// Sets a new password from a reset link's token.
    /// </summary>
    /// <remarks>
    /// On success, revokes every refresh token and trusted device for the user, so every other
    /// logged-in session — and any "remembered" 2FA device — dies immediately. A reset is assumed to
    /// mean the account may be compromised.
    /// </remarks>
    /// <response code="204">Password reset.</response>
    /// <response code="400">Invalid or expired link, or a password that fails the rules.</response>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

    /// <summary>
    /// Signs in from a Google Identity Services ID token, auto-registering a new account or
    /// auto-linking to an existing one that shares the (Google-verified) email.
    /// </summary>
    /// <remarks>
    /// Never returns a 2FA challenge: a federated Google sign-in is its own trust anchor and bypasses
    /// local email-code 2FA. The linking only happens when Google reports the email as verified, so a
    /// federated login can't hijack a local account by claiming its address.
    /// </remarks>
    /// <response code="200">Signed in. Access token in the body, refresh cookie set.</response>
    /// <response code="401">Invalid token, unverified Google email, or an inactive account.</response>
    [HttpPost("google")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request)
    {
        var (response, rawRefreshToken) = await _authService.GoogleLoginAsync(
            request.IdToken,
            request.Username
        );
        // Null on a first call that needs a username — no account exists yet, so there is nothing to
        // hold a session for. Same shape as the 2FA-challenge branch of Login.
        if (rawRefreshToken is not null)
            SetRefreshCookie(rawRefreshToken);
        return Ok(response);
    }

    /// <summary>
    /// Changes the caller's password after verifying the current one. For a 2FA-enabled account this
    /// is a two-step, step-up-gated flow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If 2FA is on and no <c>code</c> is supplied, the response comes back with
    /// <c>requiresCode: true</c> and an email is sent — nothing changes yet. The caller re-submits
    /// with the code to actually change the password. This step-up gate closes the gap where a
    /// 30-day trusted-device cookie plus a phished password could otherwise sail through a
    /// password-only check.
    /// </para>
    /// <para>
    /// On the real change, every other session (refresh tokens + trusted devices) is revoked and the
    /// caller gets fresh tokens so they alone stay signed in.
    /// </para>
    /// </remarks>
    /// <response code="200">Either <c>requiresCode: true</c> (step-up pending) or a completed change with new tokens.</response>
    /// <response code="401">Wrong current password.</response>
    /// <response code="400">A passwordless (Google-only) account — set a password first — or a bad step-up code.</response>
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(ChangePasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

    /// <summary>
    /// Starts an email change after verifying the password. For a 2FA-enabled account, step-up-gated
    /// exactly like change-password.
    /// </summary>
    /// <remarks>
    /// With 2FA on and no code yet, this emails a step-up code and returns <c>requiresCode: true</c>
    /// without sending the change-email link. Once past that gate, it emails a confirmation link to
    /// the NEW address; the current email stays active until that link is followed
    /// (<c>change-email/confirm</c>), so a mistaken or malicious change can't silently lock the owner
    /// out.
    /// </remarks>
    /// <response code="200"><c>requiresCode</c> — true when a step-up code is pending, false once the link was sent.</response>
    /// <response code="401">Wrong password.</response>
    /// <response code="400">Passwordless account, an email already in use, or a bad step-up code.</response>
    [HttpPost("change-email/request")]
    [Authorize]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(ChangeEmailRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
