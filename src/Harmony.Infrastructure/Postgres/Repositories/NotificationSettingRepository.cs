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

    public async Task<List<long>> GetOptedIntoAllAsync(long guildId, long channelId)
    {
        // One query for both scopes; resolve channel-over-guild in memory. The opted-in set is
        // small (mentions is the default), so the post-filter is cheap.
        var rows = await _db
            .NotificationSettings.AsNoTracking()
            .Where(s =>
                (s.ScopeType == NotificationScope.Guild && s.ScopeId == guildId)
                || (s.ScopeType == NotificationScope.Channel && s.ScopeId == channelId)
            )
            .ToListAsync();

        var channelLevel = new Dictionary<long, string>();
        var guildLevel = new Dictionary<long, string>();
        foreach (var r in rows)
        {
            if (r.ScopeType == NotificationScope.Channel)
                channelLevel[r.UserId] = r.Level;
            else
                guildLevel[r.UserId] = r.Level;
        }

        var result = new List<long>();
        foreach (var uid in channelLevel.Keys.Union(guildLevel.Keys))
        {
            var level = channelLevel.TryGetValue(uid, out var cl)
                ? cl
                : guildLevel.GetValueOrDefault(uid, NotificationLevel.Default);
            if (level == NotificationLevel.All)
                result.Add(uid);
        }
        return result;
    }

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

    public async Task UpsertSuppressEveryoneAsync(
        long userId,
        string scopeType,
        long scopeId,
        bool value
    )
    {
        var existing = await _db.NotificationSettings.FirstOrDefaultAsync(s =>
            s.UserId == userId && s.ScopeType == scopeType && s.ScopeId == scopeId
        );
        if (existing is null)
        {
            // No row yet: create one at the default level carrying just this flag.
            await _db.NotificationSettings.AddAsync(
                new NotificationSetting
                {
                    UserId = userId,
                    ScopeType = scopeType,
                    ScopeId = scopeId,
                    Level = NotificationLevel.Default,
                    SuppressEveryone = value,
                }
            );
        }
        else
        {
            existing.SuppressEveryone = value;
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
