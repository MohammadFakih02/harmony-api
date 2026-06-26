using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class GuildBanRepository : IGuildBanRepository
{
    private readonly HarmonyDbContext _db;

    public GuildBanRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsBannedAsync(long guildId, long userId) =>
        await _db.GuildBans.AsNoTracking().AnyAsync(b => b.GuildId == guildId && b.UserId == userId);

    public async Task<GuildBan?> GetAsync(long guildId, long userId) =>
        await _db.GuildBans.FirstOrDefaultAsync(b => b.GuildId == guildId && b.UserId == userId);

    public async Task<List<GuildBan>> GetByGuildAsync(long guildId) =>
        await _db
            .GuildBans.AsNoTracking()
            .Where(b => b.GuildId == guildId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

    public async Task AddAsync(GuildBan ban) => await _db.GuildBans.AddAsync(ban);

    public void Remove(GuildBan ban) => _db.GuildBans.Remove(ban);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
