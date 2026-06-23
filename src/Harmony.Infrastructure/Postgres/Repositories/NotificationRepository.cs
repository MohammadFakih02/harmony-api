using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class NotificationRepository : INotificationRepository
{
    private readonly HarmonyDbContext _db;

    public NotificationRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Notification notification) =>
        await _db.Notifications.AddAsync(notification);

    public async Task<Notification?> GetByIdForUserAsync(long notificationId, long userId) =>
        await _db.Notifications.FirstOrDefaultAsync(n =>
            n.Id == notificationId && n.UserId == userId
        );

    public async Task<List<Notification>> GetForUserAsync(long userId, int limit) =>
        await _db
            .Notifications.Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<int> GetUnreadCountAsync(long userId) =>
        await _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

    public async Task<bool> DeleteForUserAsync(long notificationId, long userId)
    {
        var deleted = await _db
            .Notifications.Where(n => n.Id == notificationId && n.UserId == userId)
            .ExecuteDeleteAsync();
        return deleted > 0;
    }

    public async Task MarkAllReadAsync(long userId) =>
        await _db
            .Notifications.Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
