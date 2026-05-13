using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class GuildRepository : IGuildRepository
{
    private readonly HarmonyDbContext _db;

    public GuildRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<Guild?> GetByIdAsync(long guildId) =>
        await _db
            .Guilds.Include(g => g.Channels)
            .Include(g => g.Roles)
            .FirstOrDefaultAsync(g => g.Id == guildId);

    public async Task<Guild?> GetByInviteCodeAsync(string inviteCode) =>
        await _db.Guilds.FirstOrDefaultAsync(g => g.InviteCode == inviteCode);

    public async Task<List<Guild>> GetByUserIdAsync(long userId) =>
        await _db
            .GuildMembers.Where(m => m.UserId == userId)
            .Include(m => m.Guild)
            .Select(m => m.Guild)
            .ToListAsync();

    public async Task<bool> IsMemberAsync(long guildId, long userId) =>
        await _db.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId);

    public async Task<GuildMember?> GetMemberAsync(long guildId, long userId) =>
        await _db
            .GuildMembers.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId);

    public async Task<List<GuildMember>> GetMembersAsync(long guildId) =>
        await _db
            .GuildMembers.Where(m => m.GuildId == guildId)
            .Include(m => m.User)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync();

    public async Task AddAsync(Guild guild)
    {
        await _db.Guilds.AddAsync(guild);
    }

    public async Task AddMemberAsync(GuildMember member)
    {
        await _db.GuildMembers.AddAsync(member);
    }

    public void RemoveMember(GuildMember member)
    {
        _db.GuildMembers.Remove(member);
    }

    public Task RemoveMemberAsync(GuildMember member)
    {
        _db.GuildMembers.Remove(member);
        return Task.CompletedTask;
    }

    public void Delete(Guild guild)
    {
        _db.Guilds.Remove(guild);
    }

    public Task DeleteAsync(Guild guild)
    {
        _db.Guilds.Remove(guild);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
