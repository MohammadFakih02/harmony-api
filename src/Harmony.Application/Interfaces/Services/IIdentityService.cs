using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Services;

/// <summary>
/// Thin wrapper over ASP.NET Core Identity for the operations Harmony needs: account creation, lookups,
/// password checks, email confirmation, 2FA enablement, password reset, external (Google) logins, and the
/// credential-change flows. Success/failure is reported as an <c>(bool Succeeded, string[] Errors)</c>
/// tuple rather than by throwing.
/// </summary>
public interface IIdentityService
{
    /// <summary>Creates a user with a local password (hashed by Identity).</summary>
    Task<(bool Succeeded, string[] Errors)> CreateUserAsync(User user, string password);

    /// <summary>Finds a user by email, or null.</summary>
    Task<User?> FindByEmailAsync(string email);

    /// <summary>Finds a user by username, or null.</summary>
    Task<User?> FindByNameAsync(string username);

    /// <summary>Finds a user by snowflake id, or null.</summary>
    Task<User?> FindByIdAsync(long id);

    /// <summary>Verifies a plaintext password against the user's stored hash.</summary>
    Task<bool> CheckPasswordAsync(User user, string password);

    /// <summary>Generates a token for the verify-email flow (sent to the user by email).</summary>
    Task<string> GenerateEmailConfirmationTokenAsync(User user);

    /// <summary>Confirms an email address given a token from <see cref="GenerateEmailConfirmationTokenAsync"/>.</summary>
    Task<bool> ConfirmEmailAsync(User user, string token);

    /// <summary>Toggles the account's two-factor flag (email-code 2FA; no TOTP).</summary>
    Task SetTwoFactorEnabledAsync(User user, bool enabled);

    /// <summary>Generates a single-use forgot-password token (sent to the user by email).</summary>
    Task<string> GeneratePasswordResetTokenAsync(User user);

    /// <summary>Resets the password given a token from <see cref="GeneratePasswordResetTokenAsync"/>.</summary>
    Task<(bool Succeeded, string[] Errors)> ResetPasswordAsync(User user, string token, string newPassword);

    // --- Google sign-in (Stage D) ---

    /// <summary>Finds the user linked to a Google account (by Google <c>sub</c> claim), or null.</summary>
    Task<User?> FindByGoogleLoginAsync(string subject);

    /// <summary>Links a Google account (its <c>sub</c> claim) to an existing user.</summary>
    Task LinkGoogleLoginAsync(User user, string subject);

    /// <summary>Creates a user with no local password (external-login-only account).</summary>
    Task<(bool Succeeded, string[] Errors)> CreateUserWithoutPasswordAsync(User user);

    // --- Credential changes (Stage E) ---

    /// <summary>Changes the password, verifying the current one first.</summary>
    Task<(bool Succeeded, string[] Errors)> ChangePasswordAsync(User user, string currentPassword, string newPassword);

    /// <summary>Adds a local password to an account that has none (e.g. Google-only). Fails if one already exists.</summary>
    Task<(bool Succeeded, string[] Errors)> AddPasswordAsync(User user, string newPassword);

    /// <summary>Changes the username (cosmetic credential; uniqueness enforced by Identity).</summary>
    Task<(bool Succeeded, string[] Errors)> SetUserNameAsync(User user, string newUsername);

    /// <summary>Generates a token authorizing a change to <paramref name="newEmail"/> (emailed to the new address).</summary>
    Task<string> GenerateChangeEmailTokenAsync(User user, string newEmail);

    /// <summary>Applies an email change given a token from <see cref="GenerateChangeEmailTokenAsync"/>.</summary>
    Task<(bool Succeeded, string[] Errors)> ChangeEmailAsync(User user, string newEmail, string token);
}
