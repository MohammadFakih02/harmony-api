using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class NotificationSettingRepository : INotificationSettingRepository
{
    private readonly HarmonyDbContext _db;

    public NotificationSettingRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<NotificationSetting?> GetAsync(long userId, string scopeType, long scopeId) =>
        await _db.NotificationSettings.FirstOrDefaultAsync(s =>
            s.UserId == userId && s.ScopeType == scopeType && s.ScopeId == scopeId
        );

    public async Task<List<NotificationSetting>> GetManyAsync(
        long userId,
        string scopeType,
        IEnumerable<long> scopeIds
    )
    {
        var ids = scopeIds.ToList();
        return await _db
            .NotificationSettings.AsNoTracking()
            .Where(s => s.UserId == userId && s.ScopeType == scopeType && ids.Contains(s.ScopeId))
            .ToListAsync();
    }

    public async Task<List<NotificationSetting>> GetForResolutionAsync(
        List<long> userIds,
        long guildId,
        long channelId
    ) =>
        await _db
            .NotificationSettings.AsNoTracking()
            .Where(s =>
                userIds.Contains(s.UserId)
                && (
                    (s.ScopeType == NotificationScope.Guild && s.ScopeId == guildId)
                    || (s.ScopeType == NotificationScope.Channel && s.ScopeId == channelId)
                )
            )
            .ToListAsync();

    public async Task UpsertAsync(long userId, string scopeType, long scopeId, string level)
    {
        var existing = await _db.NotificationSettings.FirstOrDefaultAsync(s =>
            s.UserId == userId && s.ScopeType == scopeType && s.ScopeId == scopeId
        );
        if (existing is null)
        {
            await _db.NotificationSettings.AddAsync(
                new NotificationSetting
                {
                    UserId = userId,
                    ScopeType = scopeType,
                    ScopeId = scopeId,
                    Level = level,
                }
            );
        }
        else
        {
            existing.Level = level;
        }
    }

    public async Task DeleteAsync(long userId, string scopeType, long scopeId)
    {
        var existing = await _db.NotificationSettings.FirstOrDefaultAsync(s =>
            s.UserId == userId && s.ScopeType == scopeType && s.ScopeId == scopeId
        );
        if (existing is not null)
            _db.NotificationSettings.Remove(existing);
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
