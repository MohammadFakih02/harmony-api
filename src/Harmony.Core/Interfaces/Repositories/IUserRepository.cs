using Harmony.Core.Domain.Entities;

namespace Harmony.Core.Interfaces.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(long userId);
    Task SaveChangesAsync();
}
