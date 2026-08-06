using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(long userId);
    Task<Dictionary<long, User>> GetByIdsAsync(IEnumerable<long> userIds);

    /// <summary>
    /// Tracked users whose preferred-status OR custom-status expiry has passed
    /// (<paramref name="now"/> = unix-ms). The caller reverts/clears the expired fields
    /// and saves — used by the status-expiry sweep.
    /// </summary>
    Task<List<User>> GetUsersWithExpiredStatusAsync(long now);

    Task SaveChangesAsync();
}
