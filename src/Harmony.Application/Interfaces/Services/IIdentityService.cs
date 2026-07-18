using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Services;

public interface IIdentityService
{
    Task<(bool Succeeded, string[] Errors)> CreateUserAsync(User user, string password);
    Task<User?> FindByEmailAsync(string email);
    Task<User?> FindByNameAsync(string username);
    Task<User?> FindByIdAsync(long id);
    Task<bool> CheckPasswordAsync(User user, string password);
    Task<string> GenerateEmailConfirmationTokenAsync(User user);
    Task<bool> ConfirmEmailAsync(User user, string token);
    Task SetTwoFactorEnabledAsync(User user, bool enabled);
    Task<string> GeneratePasswordResetTokenAsync(User user);
    Task<(bool Succeeded, string[] Errors)> ResetPasswordAsync(User user, string token, string newPassword);

    // --- Google sign-in (Stage D) ---

    Task<User?> FindByGoogleLoginAsync(string subject);
    Task LinkGoogleLoginAsync(User user, string subject);

    /// <summary>Creates a user with no local password (external-login-only account).</summary>
    Task<(bool Succeeded, string[] Errors)> CreateUserWithoutPasswordAsync(User user);

    // --- Credential changes (Stage E) ---

    Task<(bool Succeeded, string[] Errors)> ChangePasswordAsync(User user, string currentPassword, string newPassword);

    /// <summary>Adds a local password to an account that has none (e.g. Google-only). Fails if one already exists.</summary>
    Task<(bool Succeeded, string[] Errors)> AddPasswordAsync(User user, string newPassword);

    Task<(bool Succeeded, string[] Errors)> SetUserNameAsync(User user, string newUsername);
    Task<string> GenerateChangeEmailTokenAsync(User user, string newEmail);
    Task<(bool Succeeded, string[] Errors)> ChangeEmailAsync(User user, string newEmail, string token);
}
