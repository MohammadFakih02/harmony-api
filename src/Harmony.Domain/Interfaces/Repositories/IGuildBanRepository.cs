using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IGuildBanRepository
{
    /// <summary>Whether <paramref name="userId"/> is currently banned from <paramref name="guildId"/>.
    /// Checked on the invite-redeem path to block a banned user from rejoining.</summary>
    Task<bool> IsBannedAsync(long guildId, long userId);

    /// <summary>One ban row (for unban), or null if the user is not banned.</summary>
    Task<GuildBan?> GetAsync(long guildId, long userId);

    /// <summary>All of a guild's bans, newest first — for the ban-list view.</summary>
    Task<List<GuildBan>> GetByGuildAsync(long guildId);

    Task AddAsync(GuildBan ban);

    void Remove(GuildBan ban);

    Task SaveChangesAsync();
}
