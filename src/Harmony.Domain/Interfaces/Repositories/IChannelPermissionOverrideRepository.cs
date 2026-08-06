using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IChannelPermissionOverrideRepository
{
    /// <summary>All permission overrides (role- and member-targeted) defined on a channel.</summary>
    Task<List<ChannelPermissionOverride>> GetByChannelAsync(long channelId);

    /// <summary>
    /// The single override targeting <paramref name="targetId"/> on <paramref name="channelId"/>,
    /// or null. Snowflake ids are globally unique, so (channel, target) identifies one row.
    /// </summary>
    Task<ChannelPermissionOverride?> GetByChannelAndTargetAsync(long channelId, long targetId);

    Task AddAsync(ChannelPermissionOverride o);

    void Remove(ChannelPermissionOverride o);

    Task SaveChangesAsync();
}
