using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IGuildRepository
{
    Task<Guild?> GetByIdAsync(long guildId);
    Task<Guild?> GetByInviteCodeAsync(string inviteCode);
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
    Task SaveChangesAsync();
}
