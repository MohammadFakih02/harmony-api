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

    public async Task<List<ChannelPermissionOverride>> GetByChannelAsync(long channelId) =>
        await _db
            .ChannelPermissionOverrides.Where(o => o.ChannelId == channelId)
            .ToListAsync();
}
