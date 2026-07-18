namespace Harmony.Application.Interfaces.Services;

/// <summary>The claims we trust out of a verified Google ID token.</summary>
public record GoogleUserInfo(string Subject, string Email, bool EmailVerified, string? Name);

/// <summary>
/// SDK-free seam over Google ID-token verification — the Infrastructure implementation
/// (Google.Apis.Auth) is the only code touching that library, same containment pattern as
/// IEmailSender/IWebPushSender.
/// </summary>
public interface IGoogleTokenVerifier
{
    /// <summary>Verifies a Google Identity Services ID token's signature and audience. Returns
    /// null (never throws) for an invalid, expired, or wrong-audience token — the caller decides
    /// how to surface that.</summary>
    Task<GoogleUserInfo?> VerifyAsync(string idToken, CancellationToken ct = default);
}
