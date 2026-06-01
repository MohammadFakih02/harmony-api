using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly HarmonyDbContext _db;

    public RefreshTokenRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<RefreshToken?> GetByTokenHashAsync(
        string tokenHash,
        CancellationToken ct = default
    ) =>
        await _db
            .RefreshTokens.Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);

    public async Task<List<RefreshToken>> GetActiveFamilyTokensAsync(
        Guid familyId,
        CancellationToken ct = default
    ) =>
        await _db
            .RefreshTokens.Where(r => r.FamilyId == familyId && r.RevokedAt == null)
            .ToListAsync(ct);

    public async Task AddAsync(RefreshToken token, CancellationToken ct = default) =>
        await _db.RefreshTokens.AddAsync(token, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
