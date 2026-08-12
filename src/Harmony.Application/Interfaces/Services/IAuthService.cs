namespace Harmony.Domain.Interfaces.Services;

using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;

public interface IAuthService
{
    Task<(AuthResponse response, string rawRefreshToken)> RegisterAsync(
        RegisterRequest request,
        CancellationToken ct = default
    );

    /// <summary>Logs in with a password. <paramref name="trustedDeviceToken"/> is the raw
    /// "trusted_device" cookie value, if any — a match lets a 2FA-enabled account skip the
    /// challenge. Returns a null <c>rawRefreshToken</c> (no refresh cookie should be set) when
    /// <see cref="LoginResponse.TwoFactorRequired"/> comes back true.</summary>
    Task<(LoginResponse response, string? rawRefreshToken)> LoginAsync(
        LoginRequest request,
        string? trustedDeviceToken,
        CancellationToken ct = default
    );

    Task<(AuthResponse response, string rawRefreshToken)> RefreshAsync(
        string rawRefreshToken,
        CancellationToken ct = default
    );

    Task LogoutAsync(string rawRefreshToken, CancellationToken ct = default);

    /// <summary>Completes a login challenge. Throws AuthenticationException on an invalid/expired/
    /// too-many-attempts code. <c>trustedDeviceToken</c> is non-null only when the caller asked to
    /// remember this device — the controller sets it as the "trusted_device" cookie.</summary>
    Task<(LoginResponse response, string rawRefreshToken, string? trustedDeviceToken)> Verify2faAsync(
        Verify2faRequest request,
        CancellationToken ct = default
    );

    /// <summary>Resends the login-challenge code. Returns true unless the send itself genuinely
    /// failed (an unknown/expired challenge or an active cooldown are treated as a no-op success —
    /// same "204 either way" philosophy as <see cref="RequestEmailVerificationAsync"/>).</summary>
    Task<bool> Resend2faAsync(string challengeToken, CancellationToken ct = default);

    /// <summary>Starts the enable-2FA flow: verifies the password, requires a verified email,
    /// and emails a setup code. Same true/false send-outcome contract as <see cref="Resend2faAsync"/>.</summary>
    Task<bool> Enable2faRequestAsync(long userId, string password, CancellationToken ct = default);

    /// <summary>Confirms the emailed setup code and flips TwoFactorEnabled on. Throws
    /// InvalidOperationException on an invalid/expired/too-many-attempts code.</summary>
    Task Enable2faConfirmAsync(long userId, string code, CancellationToken ct = default);

    /// <summary>Verifies the password, flips TwoFactorEnabled off, and revokes every trusted
    /// device for the user.</summary>
    Task Disable2faAsync(long userId, string password, CancellationToken ct = default);

    /// <summary>"Require 2FA on all devices again" — revokes every trusted device without
    /// touching the TwoFactorEnabled flag itself.</summary>
    Task ClearTrustedDevicesAsync(long userId, CancellationToken ct = default);

    /// <summary>(Re)sends the verification email for the given user. No-op (never throws) when
    /// already confirmed or when a send cooldown is active — always a 204 to the caller.</summary>
    Task<bool> RequestEmailVerificationAsync(long userId, CancellationToken ct = default);

    /// <summary>Confirms an email using an Identity confirmation token. Returns false (never
    /// throws) for an unknown user or an invalid/expired token.</summary>
    Task<bool> ConfirmEmailAsync(string userId, string token, CancellationToken ct = default);

    /// <summary>(Maybe) sends a password-reset email. Never throws and never reveals whether the
    /// email belongs to an account — an unknown email, an inactive cooldown, and a genuine send
    /// failure are all silent no-ops from the caller's point of view (always 204).</summary>
    Task ForgotPasswordAsync(string email, CancellationToken ct = default);

    /// <summary>Resets a password using an Identity reset token. On success, revokes every refresh
    /// token (all sessions logged out) and every trusted device for the user. Returns false (never
    /// throws) for an unknown user, an invalid/expired token, or a password that fails Identity's
    /// rules.</summary>
    Task<bool> ResetPasswordAsync(
        string userId,
        string token,
        string newPassword,
        CancellationToken ct = default
    );

    /// <summary>Signs in (or auto-links / registers) a user from a verified Google ID token.
    /// Never returns a 2FA challenge — a federated Google sign-in bypasses local email-code 2FA.
    /// <para>
    /// When the token resolves to no existing account and <paramref name="username"/> is null,
    /// NOTHING is created: the returned <see cref="LoginResponse.NeedsUsername"/> is true, the
    /// refresh token is null, and the caller is expected to re-invoke with the same token plus a
    /// chosen name. This is why the refresh token is nullable — the sign-in is incomplete, exactly
    /// as it is for an unanswered 2FA challenge.
    /// </para>
    /// Throws AuthenticationException for an invalid token, an unverified Google email, or an
    /// inactive account; ConflictException when the chosen username is taken; ArgumentException
    /// when it fails validation.</summary>
    Task<(LoginResponse response, string? rawRefreshToken)> GoogleLoginAsync(
        string idToken,
        string? username = null,
        CancellationToken ct = default
    );

    // --- Credential changes (Stage E) ---

    /// <summary>Verifies the current password, then — for a 2FA-enabled account, with no
    /// <paramref name="code"/> yet — emails a step-up code and returns
    /// <see cref="ChangePasswordResponse.RequiresCode"/> true without changing anything. Otherwise
    /// (2FA disabled, or a valid code was supplied) changes the password, revokes every other
    /// session (refresh tokens + trusted devices), and re-issues fresh tokens so the caller stays
    /// signed in — <c>rawRefreshToken</c> is null exactly when the response still requires a code.
    /// Throws AuthenticationException for wrong credentials, InvalidOperationException
    /// ("Set a password first.") for a passwordless (Google-only) account, or InvalidOperationException
    /// for an invalid/expired/too-many-attempts step-up code.</summary>
    Task<(ChangePasswordResponse response, string? rawRefreshToken)> ChangePasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        string? code,
        CancellationToken ct = default
    );

    /// <summary>Adds a local password to a passwordless (Google-only) account. Throws
    /// InvalidOperationException if a password already exists.</summary>
    Task SetPasswordAsync(long userId, string newPassword, CancellationToken ct = default);

    /// <summary>Verifies the password and the new email's availability, then — for a 2FA-enabled
    /// account, with no <paramref name="code"/> yet — emails a step-up code and returns true
    /// (RequiresCode) without sending the actual change-email confirmation link. Otherwise (2FA
    /// disabled, or a valid code was supplied) sends the confirmation link to the NEW address (the
    /// old email stays active until confirmed) and returns false. Cooldown-gated at each step.
    /// Throws AuthenticationException for wrong credentials, ConflictException for an already-in-use
    /// email, and InvalidOperationException for a passwordless account or an invalid/expired/
    /// too-many-attempts step-up code.</summary>
    Task<bool> ChangeEmailRequestAsync(
        long userId,
        string password,
        string newEmail,
        string? code,
        CancellationToken ct = default
    );

    /// <summary>Confirms an email change using the token bound to the new address. Returns false
    /// (never throws) for an unknown user or an invalid/expired/tampered token.</summary>
    Task<bool> ConfirmEmailChangeAsync(
        string userId,
        string email,
        string token,
        CancellationToken ct = default
    );

    /// <summary>Verifies the password, renames the user, and best-effort broadcasts the new
    /// username live to guilds/friends/own-tabs. Throws AuthenticationException for wrong
    /// credentials, ConflictException for a name already taken by another user, and
    /// InvalidOperationException for a passwordless account.</summary>
    Task ChangeUsernameAsync(
        long userId,
        string password,
        string newUsername,
        CancellationToken ct = default
    );
}
