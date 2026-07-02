using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class RoleRepository : IRoleRepository
{
    private readonly HarmonyDbContext _db;

    public RoleRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<Role?> GetDefaultRoleAsync(long guildId) =>
        await _db.GuildRoles.FirstOrDefaultAsync(r => r.GuildId == guildId && r.IsDefault);

    public async Task<List<Role>> GetMemberRolesAsync(long guildId, long userId) =>
        await _db
            .RoleAssignments.Where(a => a.GuildId == guildId && a.UserId == userId)
            .Join(
                _db.GuildRoles,
                a => a.RoleId,
                r => r.Id,
                (a, r) => r
            )
            .ToListAsync();

    public async Task<Role?> GetByIdAsync(long roleId) =>
        await _db.GuildRoles.FirstOrDefaultAsync(r => r.Id == roleId);

    public async Task<List<Role>> GetByGuildAsync(long guildId) =>
        await _db
            .GuildRoles.Where(r => r.GuildId == guildId)
            // Position desc = highest rank first; id desc breaks ties (newest of an equal rank first).
            .OrderByDescending(r => r.Position)
            .ThenByDescending(r => r.Id)
            .ToListAsync();

    public async Task AddAsync(Role role)
    {
        await _db.GuildRoles.AddAsync(role);
    }

    public void Remove(Role role) => _db.GuildRoles.Remove(role);

    public async Task<List<long>> GetMemberRoleIdsAsync(long guildId, long userId) =>
        await _db
            .RoleAssignments.Where(a => a.GuildId == guildId && a.UserId == userId)
            .Select(a => a.RoleId)
            .ToListAsync();

    public async Task<Dictionary<long, List<long>>> GetRoleIdsByMemberAsync(long guildId)
    {
        var rows = await _db
            .RoleAssignments.AsNoTracking()
            .Where(a => a.GuildId == guildId)
            .Select(a => new { a.UserId, a.RoleId })
            .ToListAsync();

        return rows
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RoleId).ToList());
    }

    public async Task<List<long>> GetMemberIdsWithRoleAsync(long guildId, long roleId) =>
        await _db
            .RoleAssignments.AsNoTracking()
            .Where(a => a.GuildId == guildId && a.RoleId == roleId)
            .Select(a => a.UserId)
            .ToListAsync();

    public async Task<RoleAssignment?> GetAssignmentAsync(long roleId, long userId) =>
        await _db.RoleAssignments.FirstOrDefaultAsync(a => a.RoleId == roleId && a.UserId == userId);

    public async Task AddAssignmentAsync(RoleAssignment assignment) =>
        await _db.RoleAssignments.AddAsync(assignment);

    public void RemoveAssignment(RoleAssignment assignment) =>
        _db.RoleAssignments.Remove(assignment);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
