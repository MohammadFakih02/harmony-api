using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IGuildRepository
{
    Task<Guild?> GetByIdAsync(long guildId);
    Task<List<Guild>> GetByUserIdAsync(long userId);
    Task<bool> IsMemberAsync(long guildId, long userId);
    Task<GuildMember?> GetMemberAsync(long guildId, long userId);
    Task<List<GuildMember>> GetMembersAsync(long guildId);
    Task AddAsync(Guild guild);
    Task AddMemberAsync(GuildMember member);
    Task RemoveMemberAsync(GuildMember member);
    Task DeleteAsync(Guild guild);

    /// <summary>
    /// Returns just the user ids of a guild's members — no User include, no order.
    /// Hot-path lean variant for the unread fan-out. Backed by IX_GuildMembers_guild_id.
    /// </summary>
    Task<List<long>> GetMemberIdsAsync(long guildId);

    /// <summary>
    /// Returns the ids of every guild the user is a member of — lean (id-only, no tracking).
    /// Used to fan presence (online/offline/status) out to the user's guild groups so co-members
    /// see their status live, not just their friends.
    /// </summary>
    Task<List<long>> GetGuildIdsForUserAsync(long userId);

    /// <summary>
    /// Discoverable (is_public) guilds, biggest first, optionally name-filtered.
    /// Read-only (no tracking); capped by <paramref name="limit"/>.
    /// </summary>
    Task<List<Guild>> GetPublicGuildsAsync(string? query, int limit);

    /// <summary>
    /// True if the two users are members of at least one common guild. Backs the
    /// "guild_members" DM-privacy audience — an EXISTS join, not a full id-list comparison.
    /// </summary>
    Task<bool> ShareAnyGuildAsync(long userA, long userB);

    Task SaveChangesAsync();
}
