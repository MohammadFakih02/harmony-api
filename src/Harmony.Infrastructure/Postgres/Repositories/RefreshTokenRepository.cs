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

    public async Task RotateTokenAsync(
        RefreshToken oldToken,
        RefreshToken newToken,
        CancellationToken ct = default
    )
    {
        // Resolve the configured execution strategy (e.g. NpgsqlRetryingExecutionStrategy)
        var strategy = _db.Database.CreateExecutionStrategy();

        // Execute the transaction inside the retriable strategy block to comply with connection resiliency
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var entry = _db.Entry(oldToken);
                if (entry.State == EntityState.Detached)
                {
                    _db.RefreshTokens.Attach(oldToken);
                }

                // Explicitly mark only the RevokedAt property as modified
                entry.Property(r => r.RevokedAt).IsModified = true;

                await _db.RefreshTokens.AddAsync(newToken, ct);
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }
}
