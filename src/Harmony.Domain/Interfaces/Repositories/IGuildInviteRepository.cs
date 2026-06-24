using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IGuildInviteRepository
{
    /// <summary>One invite by its (globally unique) code, or null. The preview/redeem paths
    /// receive only the code and discover the guild/channel from the returned row.</summary>
    Task<GuildInvite?> GetByCodeAsync(string code);

    /// <summary>All of a guild's invites, newest first — for the management list.</summary>
    Task<List<GuildInvite>> GetByGuildAsync(long guildId);

    Task AddAsync(GuildInvite invite);

    void Remove(GuildInvite invite);

    Task SaveChangesAsync();
}
