using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Identity;

namespace Harmony.Application.Services;

/// <inheritdoc cref="IIdentityService"/>
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

    public async Task<User?> FindByIdAsync(long id) =>
        await _userManager.FindByIdAsync(id.ToString());

    public async Task<bool> CheckPasswordAsync(User user, string password) =>
        await _userManager.CheckPasswordAsync(user, password);

    public async Task<string> GenerateEmailConfirmationTokenAsync(User user) =>
        await _userManager.GenerateEmailConfirmationTokenAsync(user);

    public async Task<bool> ConfirmEmailAsync(User user, string token) =>
        (await _userManager.ConfirmEmailAsync(user, token)).Succeeded;

    public async Task SetTwoFactorEnabledAsync(User user, bool enabled) =>
        await _userManager.SetTwoFactorEnabledAsync(user, enabled);

    public async Task<string> GeneratePasswordResetTokenAsync(User user) =>
        await _userManager.GeneratePasswordResetTokenAsync(user);

    public async Task<(bool Succeeded, string[] Errors)> ResetPasswordAsync(
        User user,
        string token,
        string newPassword
    )
    {
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }

    public async Task<User?> FindByGoogleLoginAsync(string subject) =>
        await _userManager.FindByLoginAsync("Google", subject);

    public async Task LinkGoogleLoginAsync(User user, string subject) =>
        await _userManager.AddLoginAsync(user, new UserLoginInfo("Google", subject, "Google"));

    public async Task<(bool Succeeded, string[] Errors)> CreateUserWithoutPasswordAsync(User user)
    {
        var result = await _userManager.CreateAsync(user);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }

    public async Task<(bool Succeeded, string[] Errors)> ChangePasswordAsync(
        User user,
        string currentPassword,
        string newPassword
    )
    {
        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }

    public async Task<(bool Succeeded, string[] Errors)> AddPasswordAsync(User user, string newPassword)
    {
        var result = await _userManager.AddPasswordAsync(user, newPassword);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }

    public async Task<(bool Succeeded, string[] Errors)> SetUserNameAsync(User user, string newUsername)
    {
        var result = await _userManager.SetUserNameAsync(user, newUsername);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }

    public async Task<string> GenerateChangeEmailTokenAsync(User user, string newEmail) =>
        await _userManager.GenerateChangeEmailTokenAsync(user, newEmail);

    public async Task<(bool Succeeded, string[] Errors)> ChangeEmailAsync(User user, string newEmail, string token)
    {
        var result = await _userManager.ChangeEmailAsync(user, newEmail, token);
        return (result.Succeeded, result.Errors.Select(e => e.Description).ToArray());
    }
}