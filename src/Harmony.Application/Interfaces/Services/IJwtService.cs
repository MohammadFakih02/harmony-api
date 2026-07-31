using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Services;

/// <summary>
/// Issues and hashes authentication tokens. Access tokens are short-lived signed JWTs (validated with
/// zero clock skew); refresh tokens are opaque random strings stored only as a SHA-256 hash.
/// </summary>
public interface IJwtService
{
    /// <summary>
    /// Builds a signed JWT carrying the user's snowflake (<c>sub</c>) and username. Lifetime comes from
    /// <c>Jwt:AccessTokenExpiryMinutes</c> (default 15).
    /// </summary>
    string GenerateAccessToken(User user);

    /// <summary>Generates a cryptographically-random opaque refresh token (never a JWT).</summary>
    string GenerateRefreshToken();

    /// <summary>
    /// Hashes a refresh token for at-rest storage/lookup, so a leaked database row can't be replayed.
    /// </summary>
    string HashRefreshToken(string token);
}
