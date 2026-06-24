using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class GuildInviteRepository : IGuildInviteRepository
{
    private readonly HarmonyDbContext _db;

    public GuildInviteRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<GuildInvite?> GetByCodeAsync(string code) =>
        await _db.GuildInvites.FirstOrDefaultAsync(i => i.Code == code);

    public async Task<List<GuildInvite>> GetByGuildAsync(long guildId) =>
        await _db
            .GuildInvites.Where(i => i.GuildId == guildId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

    public async Task AddAsync(GuildInvite invite) => await _db.GuildInvites.AddAsync(invite);

    public void Remove(GuildInvite invite) => _db.GuildInvites.Remove(invite);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
