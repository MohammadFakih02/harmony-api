using Harmony.Core.Domain.Entities;

namespace Harmony.Core.Interfaces.Repositories;

public interface IChannelRepository
{
    Task<Channel?> GetByIdAsync(long channelId);
    Task<List<Channel>> GetByGuildIdAsync(long guildId);
    Task AddAsync(Channel channel);
    Task DeleteAsync(Channel channel);
    Task SaveChangesAsync();
}