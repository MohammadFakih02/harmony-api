namespace Harmony.Application.Interfaces.Services;

public enum TwoFactorValidationResult
{
    Success,
    InvalidCode,
    ExpiredOrUnknown,
    TooManyAttempts,
}

/// <summary>
/// Redis-backed store for the two email-code flows that guard a 2FA-enabled account: the login
/// challenge (opaque token + code, D2) and the enable-2FA setup code (tied directly to the user —
/// only one setup can be in flight at a time). Unlike every other Redis-backed gate in this codebase
/// (which fail OPEN — a missed cooldown/dedup beats an outage), this store fails CLOSED: if Redis is
/// unreachable, every method throws rather than letting a login or a 2FA-enable slip through
/// unchallenged (D1). Codes are single-use — a successful validation deletes the underlying key.
/// </summary>
public interface ITwoFactorChallengeStore
{
    /// <summary>Starts a login challenge for the given user. Returns the opaque challenge token
    /// (handed to the client) and the plaintext code (emailed, never returned to the client).</summary>
    Task<(string ChallengeToken, string Code)> CreateChallengeAsync(
        long userId,
        CancellationToken ct = default
    );

    /// <summary>Validates a submitted code against a challenge token. Single-use on success.</summary>
    Task<(TwoFactorValidationResult Result, long? UserId)> ValidateChallengeAsync(
        string challengeToken,
        string code,
        CancellationToken ct = default
    );

    /// <summary>Mints a fresh code for a still-live challenge (resend), resetting its attempt
    /// counter and TTL, and returns it alongside the challenge's user id (the caller needs it to
    /// gate the per-user send cooldown and look up who to email). Returns null if the token is
    /// unknown/expired.</summary>
    Task<(string Code, long UserId)?> RegenerateCodeAsync(
        string challengeToken,
        CancellationToken ct = default
    );

    /// <summary>Starts an enable-2FA setup code for the given user, replacing any in-flight one.</summary>
    Task<string> CreateSetupCodeAsync(long userId, CancellationToken ct = default);

    /// <summary>Validates the code submitted to confirm enabling 2FA. Single-use on success.</summary>
    Task<TwoFactorValidationResult> ValidateSetupCodeAsync(
        long userId,
        string code,
        CancellationToken ct = default
    );

    /// <summary>Starts a purpose-scoped step-up code for an already-authenticated user (e.g.
    /// "change-password", "change-email") — a fresh 2FA confirmation gate on top of the password
    /// check, for actions where a hijacked session plus a phished/reused password shouldn't be
    /// enough to proceed unchallenged. Each purpose has its own key, so unrelated step-ups (or the
    /// enable-2FA setup code, purpose "setup") never collide.</summary>
    Task<string> CreateStepUpCodeAsync(long userId, string purpose, CancellationToken ct = default);

    /// <summary>Validates a submitted step-up code for the given purpose. Single-use on success.</summary>
    Task<TwoFactorValidationResult> ValidateStepUpCodeAsync(
        long userId,
        string purpose,
        string code,
        CancellationToken ct = default
    );
}
