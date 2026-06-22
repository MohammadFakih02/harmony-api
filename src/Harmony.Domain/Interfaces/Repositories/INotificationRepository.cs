using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface INotificationRepository
{
    /// <summary>Most recent notifications for the user, newest first.</summary>
    Task<List<Notification>> GetForUserAsync(long userId, int limit);

    /// <summary>Count only — does not materialize the rows.</summary>
    Task<int> GetUnreadCountAsync(long userId);

    /// <summary>
    /// The single notification scoped to its owner. Returns null both when the id
    /// doesn't exist and when it exists but belongs to someone else — the caller
    /// can't tell the two apart, which is the point: it bakes the ownership check
    /// into the query instead of relying on a separate check after the fetch.
    /// </summary>
    Task<Notification?> GetByIdForUserAsync(long notificationId, long userId);

    /// <summary>
    /// Bulk-marks every unread notification for the user as read in one operation —
    /// unlike single-row mark-read there's no per-row ownership ambiguity to resolve,
    /// so this doesn't need the fetch-then-mutate split GetByIdForUserAsync exists for.
    /// </summary>
    Task MarkAllReadAsync(long userId);

    Task AddAsync(Notification notification);

    Task SaveChangesAsync();
}
