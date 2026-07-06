using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class ChannelPermissionOverrideRepository : IChannelPermissionOverrideRepository
{
    private readonly HarmonyDbContext _db;

    public ChannelPermissionOverrideRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    // Read-only (permission resolution + the list endpoint) — the upsert path mutates rows
    // fetched via GetByChannelAndTargetAsync, which stays tracked.
    public async Task<List<ChannelPermissionOverride>> GetByChannelAsync(long channelId) =>
        await _db
            .ChannelPermissionOverrides.AsNoTracking()
            .Where(o => o.ChannelId == channelId)
            .ToListAsync();

    public async Task<ChannelPermissionOverride?> GetByChannelAndTargetAsync(
        long channelId,
        long targetId
    ) =>
        await _db.ChannelPermissionOverrides.FirstOrDefaultAsync(o =>
            o.ChannelId == channelId && o.TargetId == targetId
        );

    public async Task AddAsync(ChannelPermissionOverride o) =>
        await _db.ChannelPermissionOverrides.AddAsync(o);

    public void Remove(ChannelPermissionOverride o) =>
        _db.ChannelPermissionOverrides.Remove(o);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
