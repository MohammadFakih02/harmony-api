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
            .OrderByDescending(r => r.Position)
            .ToListAsync();

    public async Task AddAsync(Role role)
    {
        await _db.GuildRoles.AddAsync(role);
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
