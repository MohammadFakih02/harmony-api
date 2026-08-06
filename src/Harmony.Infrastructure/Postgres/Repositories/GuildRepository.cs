using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class GuildRepository : IGuildRepository
{
    private readonly HarmonyDbContext _db;

    public GuildRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    // Normal reads exclude soft-deleted guilds (§5.71 #5) — a deleted guild 404s everywhere and drops
    // off every member's rail; only Trash/restore reach it, via the *IncludingDeleted variants below.
    public async Task<Guild?> GetByIdAsync(long guildId) =>
        await _db
            .Guilds.Include(g => g.Channels)
            .Include(g => g.Roles)
            .FirstOrDefaultAsync(g => g.Id == guildId && g.DeletedAt == null);

    public async Task<List<Guild>> GetByUserIdAsync(long userId) =>
        await _db
            .GuildMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Guild.DeletedAt == null)
            // Deterministic join order (snowflake id breaks same-ms ties) — the base order the
            // user's personal guild_order is applied over, and where unranked guilds land.
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.GuildId)
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
            .GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId)
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

    public async Task RemoveMemberAsync(GuildMember member)
    {
        _db.GuildMembers.Remove(member);

        // Drop the member's role assignments too, so rejoining resets them to @everyone rather than
        // restoring their old roles/permissions. RoleAssignments FK to (User, Role), not GuildMember,
        // so they don't cascade with the membership — this covers kick, ban, and leave in one place.
        await _db
            .RoleAssignments.Where(a => a.GuildId == member.GuildId && a.UserId == member.UserId)
            .ExecuteDeleteAsync();
    }

    public async Task AdjustMemberCountAsync(long guildId, int delta)
    {
        var guilds = _db.Guilds.Where(g => g.Id == guildId);

        // Clamp in the predicate rather than with a GREATEST expression: if a double-leave race
        // would drive the count below zero, the UPDATE simply matches no rows.
        if (delta < 0)
            guilds = guilds.Where(g => g.MemberCount >= -delta);

        await guilds.ExecuteUpdateAsync(s =>
            s.SetProperty(g => g.MemberCount, g => g.MemberCount + delta)
        );
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

    public async Task<List<long>> GetMemberIdsAsync(long guildId) =>
        await _db.GuildMembers.Where(m => m.GuildId == guildId).Select(m => m.UserId).ToListAsync();

    public async Task<List<long>> GetGuildIdsForUserAsync(long userId) =>
        await _db
            .GuildMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.GuildId)
            .ToListAsync();

    public async Task<bool> ShareAnyGuildAsync(long userA, long userB) =>
        await _db
            .GuildMembers.AsNoTracking()
            .Where(m => m.UserId == userA)
            .Join(
                _db.GuildMembers.AsNoTracking().Where(m => m.UserId == userB),
                a => a.GuildId,
                b => b.GuildId,
                (a, b) => 1
            )
            .AnyAsync();

    // Loads a guild regardless of soft-delete state — for the owner's restore / permanent-delete.
    public async Task<Guild?> GetByIdIncludingDeletedAsync(long guildId) =>
        await _db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId);

    // The owner's Trash: guilds they own that are soft-deleted, newest-deleted first.
    public async Task<List<Guild>> GetDeletedByOwnerAsync(long ownerId) =>
        await _db
            .Guilds.AsNoTracking()
            .Where(g => g.OwnerId == ownerId && g.DeletedAt != null)
            .OrderByDescending(g => g.DeletedAt)
            .ToListAsync();

    // Auto-purge sweep: guilds trashed before the cutoff (unix ms), capped.
    public async Task<List<Guild>> GetPurgeableAsync(long deletedBefore, int limit) =>
        await _db
            .Guilds.Where(g => g.DeletedAt != null && g.DeletedAt < deletedBefore)
            .OrderBy(g => g.DeletedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<List<Guild>> GetPublicGuildsAsync(string? query, int limit)
    {
        var guilds = _db.Guilds.AsNoTracking().Where(g => g.IsPublic && g.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            guilds = guilds.Where(g => EF.Functions.ILike(g.Name, $"%{q}%"));
        }
        return await guilds
            .OrderByDescending(g => g.MemberCount)
            .ThenByDescending(g => g.Id)
            .Take(limit)
            .ToListAsync();
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
