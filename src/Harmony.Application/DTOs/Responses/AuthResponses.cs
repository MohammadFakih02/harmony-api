namespace Harmony.Application.DTOs.Responses;

public record AuthResponse(
    string AccessToken,
    UserResponse User
);

public record UserResponse(
    long Id,
    string Username,
    string Email,
    string? AvatarKey,
    string AccountStatus,
    bool EmailVerified,
    bool TwoFactorEnabled,
    bool HasPassword
);

/// <summary>
/// Login's response shape (D3): a flat, additive superset of the old bare-token response so the
/// non-2FA happy path still serializes a top-level <c>accessToken</c> (existing tests/clients that
/// only look for that field keep working). <c>TwoFactorRequired</c> discriminates the two shapes —
/// a "needs a code" outcome is a normal 200, not an exception (GlobalExceptionHandler would otherwise
/// map it to a 401/400). When required, only <see cref="ChallengeToken"/> is populated; the caller
/// must complete <c>POST /api/auth/2fa/verify</c> before getting real tokens.
/// </summary>
public record LoginResponse(
    string? AccessToken,
    UserResponse? User,
    bool TwoFactorRequired,
    string? ChallengeToken,
    // Google sign-in only: set when the ID token is valid but no account exists yet, so the caller
    // must come back with a chosen username before anything is created. The new members are
    // defaulted so the two factories below — and every existing positional construction — still
    // compile unchanged.
    bool NeedsUsername = false,
    string? SuggestedUsername = null,
    string? Email = null
)
{
    public static LoginResponse FromAuth(AuthResponse auth) =>
        new(auth.AccessToken, auth.User, TwoFactorRequired: false, ChallengeToken: null);

    public static LoginResponse Challenge(string challengeToken) =>
        new(AccessToken: null, User: null, TwoFactorRequired: true, ChallengeToken: challengeToken);

    /// <summary>
    /// A verified Google identity with no matching account. NOTHING has been created — no user row,
    /// no tokens — so abandoning here leaves no trace. The caller re-posts the same ID token together
    /// with a username to complete registration. <paramref name="suggestedUsername"/> is a
    /// known-free name derived from the email, offered as a prefill only; the server re-validates
    /// whatever comes back.
    /// </summary>
    public static LoginResponse UsernameRequired(string suggestedUsername, string email) =>
        new(
            AccessToken: null,
            User: null,
            TwoFactorRequired: false,
            ChallengeToken: null,
            NeedsUsername: true,
            SuggestedUsername: suggestedUsername,
            Email: email
        );
}

/// <summary>
/// Change-password's response shape (D20): mirrors <see cref="LoginResponse"/>'s discriminated-union
/// pattern. On a non-2FA account (or once the emailed code has been verified), the change is applied
/// immediately and <see cref="AccessToken"/>/<see cref="User"/> carry a fresh session (a password
/// change revokes every other session, so the caller needs new tokens to stay signed in). When the
/// account has 2FA enabled and no code was supplied yet, <see cref="RequiresCode"/> is true and
/// nothing else is populated — the password has been verified, but the change itself is on hold
/// until the emailed step-up code comes back on a follow-up call.
/// </summary>
public record ChangePasswordResponse(bool RequiresCode, string? AccessToken, UserResponse? User)
{
    public static ChangePasswordResponse Done(AuthResponse auth) =>
        new(RequiresCode: false, auth.AccessToken, auth.User);

    public static ChangePasswordResponse NeedsCode() =>
        new(RequiresCode: true, AccessToken: null, User: null);
}

/// <summary>
/// Change-email-request's response shape (D20): the password has been verified and the new email's
/// availability confirmed. If the account has 2FA enabled and no code was supplied yet,
/// <see cref="RequiresCode"/> is true and the caller must resubmit with the emailed step-up code
/// before the actual confirmation link is sent to the new address. If false, the confirmation link
/// has already been sent (either because 2FA is disabled, or because a valid code was supplied).
/// </summary>
public record ChangeEmailRequestResponse(bool RequiresCode);