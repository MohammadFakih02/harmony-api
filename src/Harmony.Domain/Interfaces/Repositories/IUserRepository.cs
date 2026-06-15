using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(long userId);
    Task<Dictionary<long, User>> GetByIdsAsync(IEnumerable<long> userIds);
    Task SaveChangesAsync();
}
