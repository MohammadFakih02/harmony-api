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
    Task SaveChangesAsync();
}