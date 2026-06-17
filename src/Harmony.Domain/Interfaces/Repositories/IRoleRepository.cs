using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IRoleRepository
{
    /// <summary>The guild's default (@everyone) role — the permission baseline for every member.</summary>
    Task<Role?> GetDefaultRoleAsync(long guildId);

    /// <summary>
    /// Roles explicitly assigned to a member via RoleAssignment (does NOT include @everyone,
    /// which applies implicitly to all members). Used to OR in additional permission bits.
    /// </summary>
    Task<List<Role>> GetMemberRolesAsync(long guildId, long userId);

    Task<Role?> GetByIdAsync(long roleId);

    Task<List<Role>> GetByGuildAsync(long guildId);

    Task AddAsync(Role role);

    Task SaveChangesAsync();
}
