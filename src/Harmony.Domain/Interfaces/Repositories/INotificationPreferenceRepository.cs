using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface INotificationPreferenceRepository
{
    /// <summary>
    /// The user's preference row, or null. No row exists for any user registered
    /// before this feature shipped (registration never created one until now) —
    /// callers must treat a null result the same as a row where every flag is at
    /// its default (all enabled), not as an error.
    /// </summary>
    Task<NotificationPreference?> GetAsync(long userId);

    /// <summary>
    /// Bulk lookup for many users at once — the mention path needs every mentioned
    /// user's preference in one query rather than one round trip per mention.
    /// Users with no row are simply absent from the result; same null-means-default
    /// handling applies as in GetAsync.
    /// </summary>
    Task<List<NotificationPreference>> GetForUsersAsync(List<long> userIds);

    Task AddAsync(NotificationPreference preference);

    Task SaveChangesAsync();
}
