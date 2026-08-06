using Google.Apis.Auth;
using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Verifies Google Identity Services ID tokens — the ONLY file touching Google.Apis.Auth (same
/// SDK-containment pattern as MailKitEmailSender/WebPushSender). Built from the "Google" config
/// section; when the Client ID is unconfigured (fresh checkout, CI) every verify attempt returns
/// null, same fail-soft posture as WebPushSender with unconfigured VAPID keys.
/// </summary>
public sealed class GoogleTokenVerifier : IGoogleTokenVerifier
{
    private readonly string _clientId;
    private readonly ILogger<GoogleTokenVerifier> _logger;

    public GoogleTokenVerifier(IConfiguration configuration, ILogger<GoogleTokenVerifier> logger)
    {
        _logger = logger;
        _clientId = configuration["Google:ClientId"] ?? "";

        if (string.IsNullOrWhiteSpace(_clientId))
            _logger.LogWarning("Google: ClientId not configured — Google sign-in disabled");
    }

    public async Task<GoogleUserInfo?> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            return null;

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { _clientId } }
            );

            return new GoogleUserInfo(payload.Subject, payload.Email, payload.EmailVerified, payload.Name);
        }
        catch (InvalidJwtException ex)
        {
            _logger.LogWarning(ex, "Google: ID token verification failed");
            return null;
        }
    }
}
