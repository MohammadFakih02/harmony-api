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
}
