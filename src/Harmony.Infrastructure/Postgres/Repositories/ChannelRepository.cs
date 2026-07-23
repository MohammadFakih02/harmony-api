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

    // Normal reads exclude soft-deleted channels (§5.71 #5) — a deleted channel is invisible to every
    // path but Trash/restore, which use the *IncludingDeleted variants below. They also exclude a
    // *live* channel whose GUILD is trashed: soft-deleting a guild only tombstones the guild row, so
    // without this a stale/deep link could still reach a deleted server's channel (send a message,
    // etc.). A DM channel (GuildId null) has no guild and is always visible.
    public async Task<Channel?> GetByIdAsync(long channelId) =>
        await _db.Channels.FirstOrDefaultAsync(c =>
            c.Id == channelId && c.DeletedAt == null && (c.Guild == null || c.Guild.DeletedAt == null)
        );

    public async Task<Channel?> GetByIdAndGuildIdAsync(long channelId, long guildId) =>
        await _db.Channels.FirstOrDefaultAsync(c =>
            c.Id == channelId
            && c.GuildId == guildId
            && c.DeletedAt == null
            && (c.Guild == null || c.Guild.DeletedAt == null)
        );

    public async Task<List<Channel>> GetByGuildIdAsync(long guildId) =>
        await _db
            .Channels.AsNoTracking()
            .Where(c =>
                c.GuildId == guildId
                && c.DeletedAt == null
                && (c.Guild == null || c.Guild.DeletedAt == null)
            )
            .OrderBy(c => c.Position)
            .ToListAsync();

    // Loads a channel regardless of soft-delete state — for restore / permanent-delete, which act
    // ON a trashed row that every normal read hides.
    public async Task<Channel?> GetByIdIncludingDeletedAsync(long channelId) =>
        await _db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);

    // The guild's Trash: its soft-deleted channels, newest-deleted first.
    public async Task<List<Channel>> GetDeletedByGuildIdAsync(long guildId) =>
        await _db
            .Channels.AsNoTracking()
            .Where(c => c.GuildId == guildId && c.DeletedAt != null)
            .OrderByDescending(c => c.DeletedAt)
            .ToListAsync();

    // Auto-purge sweep: channels trashed before the cutoff (unix ms), whose owning guild is NOT itself
    // trashed (a trashed guild's channels are purged with the guild, not individually).
    public async Task<List<Channel>> GetPurgeableAsync(long deletedBefore, int limit) =>
        await _db
            .Channels.Where(c =>
                c.DeletedAt != null
                && c.DeletedAt < deletedBefore
                && (c.Guild == null || c.Guild.DeletedAt == null)
            )
            .OrderBy(c => c.DeletedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<List<long>> GetTextChannelIdsByGuildIncludingDeletedAsync(long guildId) =>
        await _db
            .Channels.AsNoTracking()
            .Where(c => c.GuildId == guildId && c.Type == "text")
            .Select(c => c.Id)
            .ToListAsync();

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

    public async Task<Dictionary<long, long>> GetTextChannelGuildMapAsync(IEnumerable<long> guildIds)
    {
        var ids = guildIds as IReadOnlyList<long> ?? guildIds.ToList();
        if (ids.Count == 0)
            return [];

        // ToDictionaryAsync materializes full Channel entities before projecting — without
        // AsNoTracking every text channel of every guild would land in the change tracker.
        return await _db
            .Channels.AsNoTracking()
            .Where(c =>
                c.GuildId != null
                && ids.Contains(c.GuildId.Value)
                && c.Type == "text"
                && c.DeletedAt == null
                && (c.Guild == null || c.Guild.DeletedAt == null)
            )
            .ToDictionaryAsync(c => c.Id, c => c.GuildId!.Value);
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
