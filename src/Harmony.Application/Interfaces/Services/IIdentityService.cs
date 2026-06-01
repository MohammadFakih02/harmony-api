using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Services;

public interface IIdentityService
{
    Task<(bool Succeeded, string[] Errors)> CreateUserAsync(User user, string password);
    Task<User?> FindByEmailAsync(string email);
    Task<User?> FindByNameAsync(string username);
    Task<bool> CheckPasswordAsync(User user, string password);
}
