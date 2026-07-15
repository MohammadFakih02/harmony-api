using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class UserMuteRepository : IUserMuteRepository
{
    private readonly HarmonyDbContext _db;

    public UserMuteRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<UserMute?> GetAsync(long userId, long targetId, string targetType) =>
        await _db.UserMutes.FirstOrDefaultAsync(m =>
            m.UserId == userId && m.TargetId == targetId && m.TargetType == targetType
        );

    public async Task<List<UserMute>> GetActiveMutesAsync(long userId, long nowUnixMs) =>
        await _db
            .UserMutes.AsNoTracking()
            .Where(m =>
                m.UserId == userId && (m.MutedUntil == null || m.MutedUntil > nowUnixMs)
            )
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

    public async Task<bool> IsMutedAsync(
        long userId,
        long targetId,
        string targetType,
        long nowUnixMs
    ) =>
        await _db.UserMutes.AnyAsync(m =>
            m.UserId == userId
            && m.TargetId == targetId
            && m.TargetType == targetType
            && (m.MutedUntil == null || m.MutedUntil > nowUnixMs)
        );

    public async Task AddAsync(UserMute mute) => await _db.UserMutes.AddAsync(mute);

    public void Remove(UserMute mute) => _db.UserMutes.Remove(mute);

    public async Task<List<UserMute>> DeleteExpiredAsync(long nowUnixMs, int limit = 500)
    {
        // Oldest expiry first, so a capped sweep drains a backlog in the order it accrued rather
        // than starving the same tail every pass. Not ExecuteDelete: the caller needs the rows
        // themselves to broadcast MuteExpired to each owner, and that returns only a count.
        var expired = await _db
            .UserMutes.Where(m => m.MutedUntil != null && m.MutedUntil <= nowUnixMs)
            .OrderBy(m => m.MutedUntil)
            .Take(limit)
            .ToListAsync();

        if (expired.Count == 0)
            return expired;

        _db.UserMutes.RemoveRange(expired);
        await _db.SaveChangesAsync();
        return expired;
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
