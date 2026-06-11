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

    /// <summary>
    /// Executes sequential channel position updates within an explicit SQL transaction [12].
    /// Satisfies Clean Architecture by accepting basic C# tuple types instead of Application DTOs [2].
    /// </summary>
    Task ReorderAsync(IEnumerable<(long ChannelId, int Position)> updates);

    /// <summary>
    /// Returns text-channel ids across many guilds in one query — for sidebar
    /// unread load, avoiding an N+1 over the user's guilds.
    /// </summary>
    Task<List<long>> GetTextChannelIdsByGuildIdsAsync(IEnumerable<long> guildIds);
}
