using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IChannelPermissionOverrideRepository
{
    /// <summary>All permission overrides (role- and member-targeted) defined on a channel.</summary>
    Task<List<ChannelPermissionOverride>> GetByChannelAsync(long channelId);
}
