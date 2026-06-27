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

    /// <summary>Removes a role; its RoleAssignments cascade-delete via the FK.</summary>
    void Remove(Role role);

    /// <summary>The role-ids explicitly assigned to a member (excludes the implicit @everyone).</summary>
    Task<List<long>> GetMemberRoleIdsAsync(long guildId, long userId);

    /// <summary>All explicit role assignments in a guild, grouped by member — for the member-list enrichment.</summary>
    Task<Dictionary<long, List<long>>> GetRoleIdsByMemberAsync(long guildId);

    Task<RoleAssignment?> GetAssignmentAsync(long roleId, long userId);

    Task AddAssignmentAsync(RoleAssignment assignment);

    void RemoveAssignment(RoleAssignment assignment);

    Task SaveChangesAsync();
}
