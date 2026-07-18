namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Per-user, per-purpose send cooldown for outbound emails (verification / 2FA / password reset).
/// Independent of the HTTP rate limiter — the limiter is off in dev/Test (§ load-testing), so this
/// is what actually stops a resend button from being hammered in every environment.
/// </summary>
public interface IEmailCooldownGate
{
    /// <summary>Returns true and starts the cooldown if none is active; false if still cooling down.</summary>
    Task<bool> TryAcquireAsync(string purpose, long userId, CancellationToken ct = default);

    /// <summary>
    /// Refunds a cooldown claimed by <see cref="TryAcquireAsync"/> when the send it was guarding
    /// actually failed (e.g. SMTP unreachable) — otherwise the caller is silently locked out of
    /// retrying for the full window despite never having received anything. Best-effort.
    /// </summary>
    Task ReleaseAsync(string purpose, long userId, CancellationToken ct = default);
}
