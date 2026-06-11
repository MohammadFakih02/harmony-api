using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
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

    public async Task<Channel?> GetByIdAndGuildIdAsync(long channelId, long guildId) =>
        await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.GuildId == guildId);

    public async Task<List<Channel>> GetByGuildIdAsync(long guildId) =>
        await _db.Channels.Where(c => c.GuildId == guildId).OrderBy(c => c.Position).ToListAsync();

    public async Task AddAsync(Channel channel) => await _db.Channels.AddAsync(channel);

    public Task DeleteAsync(Channel channel)
    {
        _db.Channels.Remove(channel);
        return Task.CompletedTask;
    }

    public async Task ReorderAsync(IEnumerable<(long ChannelId, int Position)> updates)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                var updateMap = updates.ToDictionary(u => u.ChannelId, u => u.Position);

                var channels = await _db
                    .Channels.Where(c => updateMap.Keys.Contains(c.Id))
                    .ToListAsync();

                foreach (var channel in channels)
                {
                    if (updateMap.TryGetValue(channel.Id, out var newPosition))
                    {
                        channel.Position = newPosition;
                    }
                }

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    public async Task<List<long>> GetTextChannelIdsByGuildIdsAsync(IEnumerable<long> guildIds)
    {
        var ids = guildIds as IReadOnlyList<long> ?? guildIds.ToList();
        if (ids.Count == 0)
            return [];

        return await _db
            .Channels.Where(c =>
                c.GuildId != null && ids.Contains(c.GuildId.Value) && c.Type == "text"
            )
            .Select(c => c.Id)
            .ToListAsync();
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
