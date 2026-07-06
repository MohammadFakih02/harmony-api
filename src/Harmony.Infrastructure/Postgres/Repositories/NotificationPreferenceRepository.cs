using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class NotificationPreferenceRepository : INotificationPreferenceRepository
{
    private readonly HarmonyDbContext _db;

    public NotificationPreferenceRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<NotificationPreference?> GetAsync(long userId) =>
        await _db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId);

    // Read-only bulk fetch for mention suppression — the PATCH path mutates the single row
    // from GetAsync, which stays tracked.
    public async Task<List<NotificationPreference>> GetForUsersAsync(List<long> userIds) =>
        await _db
            .NotificationPreferences.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync();

    public async Task AddAsync(NotificationPreference preference) =>
        await _db.NotificationPreferences.AddAsync(preference);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
