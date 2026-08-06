using System.Collections.Generic;
using System.Threading.Tasks;
using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IChannelRepository
{
    Task<Channel?> GetByIdAsync(long channelId);
    Task<Channel?> GetByIdAndGuildIdAsync(long channelId, long guildId);
    Task<List<Channel>> GetByGuildIdAsync(long guildId);
    Task AddAsync(Channel channel);
    Task DeleteAsync(Channel channel);
    Task SaveChangesAsync();

    /// <summary>Loads a channel regardless of its soft-delete state — for restore / permanent-delete,
    /// which operate on a trashed row that <see cref="GetByIdAsync"/> deliberately hides. (§5.71 #5)</summary>
    Task<Channel?> GetByIdIncludingDeletedAsync(long channelId);

    /// <summary>The guild's soft-deleted channels (its Trash), newest-deleted first.</summary>
    Task<List<Channel>> GetDeletedByGuildIdAsync(long guildId);

    /// <summary>Channels trashed before <paramref name="deletedBefore"/> (unix ms) whose owning guild
    /// is not itself trashed — the 30-day auto-purge sweep's work list, capped at <paramref name="limit"/>.</summary>
    Task<List<Channel>> GetPurgeableAsync(long deletedBefore, int limit);

    /// <summary>Every text channel id of a guild, live OR soft-deleted — used when a whole guild is
    /// purged so each channel's Scylla partition + search index can be cleaned before the cascade.</summary>
    Task<List<long>> GetTextChannelIdsByGuildIncludingDeletedAsync(long guildId);

    /// <summary>
    /// Executes sequential channel position updates within an explicit SQL transaction [12].
    /// Satisfies Clean Architecture by accepting basic C# tuple types instead of Application DTOs [2].
    /// </summary>
    Task ReorderAsync(IEnumerable<(long ChannelId, int Position)> updates);

    /// <summary>
    /// Returns a channelId → guildId map for all text channels across many guilds in
    /// one query — for the sidebar unread load. The guildId lets the client roll
    /// per-channel unread counts up to per-guild badges, avoiding an N+1 over guilds.
    /// </summary>
    Task<Dictionary<long, long>> GetTextChannelGuildMapAsync(IEnumerable<long> guildIds);
}
