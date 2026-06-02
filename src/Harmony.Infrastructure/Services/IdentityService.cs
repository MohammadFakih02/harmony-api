using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Identity;

namespace Harmony.Application.Services;

public class IdentityService : IIdentityService
{
    private readonly UserManager<User> _userManager;

    public IdentityService(UserManager<User> userManager)
    {
        _userManager = userManager;
    }

    public async Task<(bool Succeeded, string[] Errors)> CreateUserAsync(User user, string password)
    {
        var result = await _userManager.CreateAsync(user, password);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }

    public async Task<User?> FindByEmailAsync(string email) =>
        await _userManager.FindByEmailAsync(email);

    public async Task<User?> FindByNameAsync(string username) =>
        await _userManager.FindByNameAsync(username);

    public async Task<bool> CheckPasswordAsync(User user, string password) =>
        await _userManager.CheckPasswordAsync(user, password);
}