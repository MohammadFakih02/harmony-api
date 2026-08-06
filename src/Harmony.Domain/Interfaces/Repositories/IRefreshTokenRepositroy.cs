using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<List<RefreshToken>> GetActiveFamilyTokensAsync(
        Guid familyId,
        CancellationToken ct = default
    );
    Task AddAsync(RefreshToken token, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    Task RotateTokenAsync(
        RefreshToken oldToken,
        RefreshToken newToken,
        CancellationToken ct = default
    );

    /// <summary>Revokes every active (non-revoked) refresh token for a user, across every family —
    /// used by password reset so every other session is logged out immediately.</summary>
    Task RevokeAllForUserAsync(long userId, CancellationToken ct = default);
}
