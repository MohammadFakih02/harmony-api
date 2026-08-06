using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class TrustedDeviceRepository : ITrustedDeviceRepository
{
    private readonly HarmonyDbContext _db;

    public TrustedDeviceRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(TrustedDevice device, CancellationToken ct = default) =>
        await _db.TrustedDevices.AddAsync(device, ct);

    public async Task<TrustedDevice?> GetValidAsync(
        long userId,
        string tokenHash,
        CancellationToken ct = default
    ) =>
        await _db.TrustedDevices.FirstOrDefaultAsync(
            d => d.UserId == userId && d.TokenHash == tokenHash && d.ExpiresAt > DateTimeOffset.UtcNow,
            ct
        );

    public async Task DeleteAllForUserAsync(long userId, CancellationToken ct = default) =>
        await _db.TrustedDevices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) => await _db.SaveChangesAsync(ct);
}
