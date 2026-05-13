using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class ChannelRepository : IChannelRepository
{
    private readonly HarmonyDbContext _db;

    public ChannelRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<Channel?> GetByIdAsync(long channelId) =>
        await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);

    public async Task<List<Channel>> GetByGuildIdAsync(long guildId) =>
        await _db.Channels
            .Where(c => c.GuildId == guildId)
            .OrderBy(c => c.Position)
            .ToListAsync();

    public async Task AddAsync(Channel channel) =>
        await _db.Channels.AddAsync(channel);

    public Task DeleteAsync(Channel channel)
    {
        _db.Channels.Remove(channel);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync() =>
        await _db.SaveChangesAsync();
}