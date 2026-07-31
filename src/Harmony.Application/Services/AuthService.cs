using System.Net;
using System.Security.Authentication;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Exceptions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Application.Services;

public class AuthService : IAuthService
{
    private readonly IIdentityService _identityService;
    private readonly IRefreshTokenRepository _tokenRepository;
    private readonly ITrustedDeviceRepository _trustedDevices;
    private readonly INotificationPreferenceRepository _notificationPreferences;
    private readonly IJwtService _jwtService;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IConfiguration _config;
    private readonly IEmailSender _emailSender;
    private readonly IEmailCooldownGate _emailCooldown;
    private readonly ITwoFactorChallengeStore _twoFactorStore;
    private readonly IGoogleTokenVerifier _googleVerifier;
    private readonly IGuildRepository _guilds;
    private readonly IFriendRepository _friends;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IIdentityService identityService,
        IRefreshTokenRepository tokenRepository,
        ITrustedDeviceRepository trustedDevices,
        INotificationPreferenceRepository notificationPreferences,
        IJwtService jwtService,
        ISnowflakeIdGenerator snowflake,
        IConfiguration config,
        IEmailSender emailSender,
        IEmailCooldownGate emailCooldown,
        ITwoFactorChallengeStore twoFactorStore,
        IGoogleTokenVerifier googleVerifier,
        IGuildRepository guilds,
        IFriendRepository friends,
        IHubBroadcaster broadcaster,
        ILogger<AuthService> logger
    )
    {
        _identityService = identityService;
        _tokenRepository = tokenRepository;
        _trustedDevices = trustedDevices;
        _notificationPreferences = notificationPreferences;
        _jwtService = jwtService;
        _snowflake = snowflake;
        _config = config;
        _emailSender = emailSender;
        _emailCooldown = emailCooldown;
        _twoFactorStore = twoFactorStore;
        _googleVerifier = googleVerifier;
        _guilds = guilds;
        _friends = friends;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task<(AuthResponse response, string rawRefreshToken)> RegisterAsync(
        RegisterRequest request,
        CancellationToken ct = default
    )
    {
        if (!IsValidEmail(request.Email))
            throw new ArgumentException("Invalid email format.");

        if (await _identityService.FindByEmailAsync(request.Email) is not null)
            throw new ConflictException("Email already in use.");

        if (await _identityService.FindByNameAsync(request.Username) is not null)
            throw new ConflictException("Username already taken.");

        var user = new User
        {
            Id = _snowflake.NextId(),
            UserName = request.Username,
            Email = request.Email,
            AccountStatus = UserAccountStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var (succeeded, errors) = await _identityService.CreateUserAsync(user, request.Password);
        if (!succeeded)
            throw new InvalidOperationException(string.Join(", ", errors));

        await _notificationPreferences.AddAsync(new NotificationPreference { UserId = user.Id });
        await _notificationPreferences.SaveChangesAsync();

        // Best-effort: an SMTP hiccup must never fail registration itself — the user can always
        // resend from Settings ▸ My Account.
        await SendVerificationEmailAsync(user, ct);

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user);
        return (new AuthResponse(accessToken, ToUserResponse(user)), rawRefreshToken);
    }

    public async Task<(LoginResponse response, string? rawRefreshToken)> LoginAsync(
        LoginRequest request,
        string? trustedDeviceToken,
        CancellationToken ct = default
    )
    {
        // Resolve by email first, then fall back to username. Both are globally unique,
        // so this is unambiguous — and it avoids an '@' heuristic (ASP.NET Identity allows
        // '@' in usernames by default, so "contains @" wouldn't reliably mean "is an email").
        var user =
            await _identityService.FindByEmailAsync(request.Identifier)
            ?? await _identityService.FindByNameAsync(request.Identifier);
        if (user is null || !await _identityService.CheckPasswordAsync(user, request.Password))
            throw new AuthenticationException("Invalid credentials.");

        if (!UserAccountStatus.IsActive(user.AccountStatus))
            throw new AuthenticationException("Account is not active.");

        if (user.TwoFactorEnabled && !await IsTrustedDeviceAsync(user.Id, trustedDeviceToken, ct))
        {
            var (challengeToken, code) = await _twoFactorStore.CreateChallengeAsync(user.Id, ct);
            // Best-effort, same as every other transactional send here — the challenge screen
            // has its own Resend, so a dropped first send isn't a dead end.
            await SendTwoFactorCodeAsync(user, code, ct);
            return (LoginResponse.Challenge(challengeToken), null);
        }

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user);
        return (LoginResponse.FromAuth(new AuthResponse(accessToken, ToUserResponse(user))), rawRefreshToken);
    }

    public async Task<(LoginResponse response, string rawRefreshToken, string? trustedDeviceToken)> Verify2faAsync(
        Verify2faRequest request,
        CancellationToken ct = default
    )
    {
        var (result, userId) = await _twoFactorStore.ValidateChallengeAsync(
            request.ChallengeToken,
            request.Code,
            ct
        );
        if (result != TwoFactorValidationResult.Success || userId is null)
        {
            var message =
                result == TwoFactorValidationResult.TooManyAttempts
                    ? "Too many attempts. Please log in again."
                    : "Invalid or expired code.";
            throw new AuthenticationException(message);
        }

        var user = await _identityService.FindByIdAsync(userId.Value);
        if (user is null || !UserAccountStatus.IsActive(user.AccountStatus))
            throw new AuthenticationException("Account is not active.");

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user);
        var trustedDeviceToken = request.RememberDevice
            ? await IssueTrustedDeviceAsync(user.Id, ct)
            : null;

        return (
            LoginResponse.FromAuth(new AuthResponse(accessToken, ToUserResponse(user))),
            rawRefreshToken,
            trustedDeviceToken
        );
    }

    public async Task<bool> Resend2faAsync(string challengeToken, CancellationToken ct = default)
    {
        var regenerated = await _twoFactorStore.RegenerateCodeAsync(challengeToken, ct);
        if (regenerated is null)
            return true; // unknown/expired challenge — not an error the resend button should surface

        var (code, userId) = regenerated.Value;
        if (!await _emailCooldown.TryAcquireAsync("2fa", userId, ct))
            return true; // already sent recently

        var user = await _identityService.FindByIdAsync(userId);
        if (user is null)
            return true;

        var sent = await SendTwoFactorCodeAsync(user, code, ct);
        if (!sent)
            await _emailCooldown.ReleaseAsync("2fa", userId, ct);

        return sent;
    }

    public async Task<bool> Enable2faRequestAsync(long userId, string password, CancellationToken ct = default)
    {
        var user = await _identityService.FindByIdAsync(userId);
        if (user is null || !await _identityService.CheckPasswordAsync(user, password))
            throw new AuthenticationException("Invalid credentials.");

        if (!user.EmailConfirmed)
            throw new InvalidOperationException(
                "Verify your email before enabling two-factor authentication."
            );

        if (!await _emailCooldown.TryAcquireAsync("2fa-setup", userId, ct))
            return true; // already sent recently — the earlier code is still live

        var code = await _twoFactorStore.CreateSetupCodeAsync(userId, ct);
        var sent = await SendTwoFactorCodeAsync(user, code, ct);
        if (!sent)
            await _emailCooldown.ReleaseAsync("2fa-setup", userId, ct);

        return sent;
    }

    public async Task Enable2faConfirmAsync(long userId, string code, CancellationToken ct = default)
    {
        var result = await _twoFactorStore.ValidateSetupCodeAsync(userId, code, ct);
        if (result != TwoFactorValidationResult.Success)
        {
            var message = result switch
            {
                TwoFactorValidationResult.TooManyAttempts => "Too many attempts. Please start over.",
                TwoFactorValidationResult.ExpiredOrUnknown => "Code expired. Please request a new one.",
                _ => "Invalid code.",
            };
            throw new InvalidOperationException(message);
        }

        var user = await _identityService.FindByIdAsync(userId);
        if (user is null)
            throw new AuthenticationException("Invalid credentials.");

        await _identityService.SetTwoFactorEnabledAsync(user, true);
    }

    public async Task Disable2faAsync(long userId, string password, CancellationToken ct = default)
    {
        var user = await _identityService.FindByIdAsync(userId);
        if (user is null || !await _identityService.CheckPasswordAsync(user, password))
            throw new AuthenticationException("Invalid credentials.");

        await _identityService.SetTwoFactorEnabledAsync(user, false);
        await _trustedDevices.DeleteAllForUserAsync(userId, ct);
    }

    public async Task ClearTrustedDevicesAsync(long userId, CancellationToken ct = default) =>
        await _trustedDevices.DeleteAllForUserAsync(userId, ct);

    public async Task<(AuthResponse response, string rawRefreshToken)> RefreshAsync(
        string rawRefreshToken,
        CancellationToken ct = default
    )
    {
        var tokenHash = _jwtService.HashRefreshToken(rawRefreshToken);
        var stored = await _tokenRepository.GetByTokenHashAsync(tokenHash, ct);

        if (stored is null)
            throw new AuthenticationException("Invalid refresh token.");

        if (stored.RevokedAt is not null)
        {
            var gracePeriod = TimeSpan.FromSeconds(30);
            if (DateTimeOffset.UtcNow - stored.RevokedAt.Value > gracePeriod)
            {
                await RevokeFamilyAsync(stored.FamilyId, ct);
                throw new AuthenticationException(
                    "Refresh token reuse detected. Please log in again."
                );
            }

            // --- GRACE PERIOD MITIGATION ---
            // If the replayed token falls within the 30-second window, it's a concurrent retry.
            // Do not rotate or generate another token. Instead, return a fresh access token
            // and an empty refresh token string so the controller doesn't overwrite the cookie.
            var graceAccessToken = _jwtService.GenerateAccessToken(stored.User);
            return (new AuthResponse(graceAccessToken, ToUserResponse(stored.User)), string.Empty);
        }

        if (stored.ExpiresAt < DateTimeOffset.UtcNow)
        {
            if (stored.RevokedAt is null)
            {
                stored.RevokedAt = DateTimeOffset.UtcNow;
                await _tokenRepository.SaveChangesAsync(ct);
            }
            throw new AuthenticationException("Refresh token expired.");
        }

        // Generate values for the new rotated refresh token
        var accessToken = _jwtService.GenerateAccessToken(stored.User);
        var newRawRefreshToken = _jwtService.GenerateRefreshToken();
        var refreshExpiry = int.Parse(_config["Jwt:RefreshTokenExpiryDays"] ?? "7");

        var newToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = stored.User.Id,
            TokenHash = _jwtService.HashRefreshToken(newRawRefreshToken),
            FamilyId = stored.FamilyId,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshExpiry),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Prepare modification state for Optimistic Concurrency validation (OCC)
        stored.RevokedAt = DateTimeOffset.UtcNow;

        try
        {
            // Execute atomic transaction update
            await _tokenRepository.RotateTokenAsync(stored, newToken, ct);
        }
        catch (Exception ex)
        {
            throw new AuthenticationException(
                "Token rotation conflict or transaction error. Please log in again.",
                ex
            );
        }

        return (new AuthResponse(accessToken, ToUserResponse(stored.User)), newRawRefreshToken);
    }

    public async Task LogoutAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rawRefreshToken))
            return;

        var tokenHash = _jwtService.HashRefreshToken(rawRefreshToken);
        var stored = await _tokenRepository.GetByTokenHashAsync(tokenHash, ct);

        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = DateTimeOffset.UtcNow;
            await _tokenRepository.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> RequestEmailVerificationAsync(long userId, CancellationToken ct = default)
    {
        var user = await _identityService.FindByIdAsync(userId);
        if (user is null || user.EmailConfirmed)
            return true; // nothing to send; not an error

        if (!await _emailCooldown.TryAcquireAsync("verify", userId, ct))
            return true; // already sent recently; not an error

        var sent = await SendVerificationEmailAsync(user, ct);
        if (!sent)
            await _emailCooldown.ReleaseAsync("verify", userId, ct);

        return sent;
    }

    public async Task<bool> ConfirmEmailAsync(string userId, string token, CancellationToken ct = default)
    {
        if (!long.TryParse(userId, out var id))
            return false;

        var user = await _identityService.FindByIdAsync(id);
        if (user is null)
            return false;

        return await _identityService.ConfirmEmailAsync(user, token);
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken ct = default)
    {
        var user = await _identityService.FindByEmailAsync(email);
        if (user is null)
            return; // never reveal account existence

        if (!await _emailCooldown.TryAcquireAsync("reset", user.Id, ct))
            return; // already sent recently

        var sent = await SendPasswordResetEmailAsync(user, ct);
        if (!sent)
            await _emailCooldown.ReleaseAsync("reset", user.Id, ct);
    }

    public async Task<bool> ResetPasswordAsync(
        string userId,
        string token,
        string newPassword,
        CancellationToken ct = default
    )
    {
        if (!long.TryParse(userId, out var id))
            return false;

        var user = await _identityService.FindByIdAsync(id);
        if (user is null)
            return false;

        var (succeeded, _) = await _identityService.ResetPasswordAsync(user, token, newPassword);
        if (!succeeded)
            return false;

        // Every other session must die immediately — a leaked password + a still-live refresh
        // token would otherwise let the old credential keep working after the reset.
        await _tokenRepository.RevokeAllForUserAsync(id, ct);
        await _trustedDevices.DeleteAllForUserAsync(id, ct);
        return true;
    }

    public async Task<(LoginResponse response, string rawRefreshToken)> GoogleLoginAsync(
        string idToken,
        CancellationToken ct = default
    )
    {
        var info = await _googleVerifier.VerifyAsync(idToken, ct);
        if (info is null)
            throw new AuthenticationException("Invalid Google sign-in.");

        var user = await _identityService.FindByGoogleLoginAsync(info.Subject);

        if (user is null)
        {
            user = await _identityService.FindByEmailAsync(info.Email);
            if (user is not null)
            {
                if (!info.EmailVerified)
                    throw new AuthenticationException("Your Google account's email is not verified.");

                await _identityService.LinkGoogleLoginAsync(user, info.Subject);
            }
            else
            {
                if (!info.EmailVerified)
                    throw new AuthenticationException("Your Google account's email is not verified.");

                user = new User
                {
                    Id = _snowflake.NextId(),
                    UserName = await GenerateUniqueUsernameAsync(info.Email),
                    Email = info.Email,
                    EmailConfirmed = true,
                    AccountStatus = UserAccountStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };

                var (succeeded, errors) = await _identityService.CreateUserWithoutPasswordAsync(user);
                if (!succeeded)
                    throw new InvalidOperationException(string.Join(", ", errors));

                await _notificationPreferences.AddAsync(new NotificationPreference { UserId = user.Id });
                await _notificationPreferences.SaveChangesAsync();

                await _identityService.LinkGoogleLoginAsync(user, info.Subject);
            }
        }

        if (!UserAccountStatus.IsActive(user.AccountStatus))
            throw new AuthenticationException("Account is not active.");

        // A federated Google sign-in bypasses local email-code 2FA even if it's enabled on this
        // account — Google's own authentication is the trust anchor for this path.
        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user);
        return (LoginResponse.FromAuth(new AuthResponse(accessToken, ToUserResponse(user))), rawRefreshToken);
    }

    // --- Credential changes (Stage E) ---

    public async Task<(ChangePasswordResponse response, string? rawRefreshToken)> ChangePasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        string? code,
        CancellationToken ct = default
    )
    {
        var user = await _identityService.FindByIdAsync(userId);
        if (user is null)
            throw new AuthenticationException("Invalid credentials.");

        if (user.PasswordHash is null)
            throw new InvalidOperationException("Set a password first.");

        if (!await _identityService.CheckPasswordAsync(user, currentPassword))
            throw new AuthenticationException("Invalid credentials.");

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrEmpty(code))
            {
                await RequestStepUpCodeAsync(user, "change-password", ct);
                return (ChangePasswordResponse.NeedsCode(), null);
            }

            await ValidateStepUpCodeAsync(user.Id, "change-password", code, ct);
        }

        var (succeeded, errors) = await _identityService.ChangePasswordAsync(
            user,
            currentPassword,
            newPassword
        );
        if (!succeeded)
            throw new InvalidOperationException(string.Join(", ", errors));

        // Every other session must die immediately — the reset-password sequence — but the
        // caller stays signed in via a freshly issued token pair.
        await _tokenRepository.RevokeAllForUserAsync(userId, ct);
        await _trustedDevices.DeleteAllForUserAsync(userId, ct);

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user);
        return (ChangePasswordResponse.Done(new AuthResponse(accessToken, ToUserResponse(user))), rawRefreshToken);
    }

    public async Task SetPasswordAsync(long userId, string newPassword, CancellationToken ct = default)
    {
        var user = await _identityService.FindByIdAsync(userId);
        if (user is null)
            throw new AuthenticationException("Invalid credentials.");

        if (user.PasswordHash is not null)
            throw new InvalidOperationException("A password has been set for this account.");

        var (succeeded, errors) = await _identityService.AddPasswordAsync(user, newPassword);
        if (!succeeded)
            throw new InvalidOperationException(string.Join(", ", errors));
    }

    public async Task<bool> ChangeEmailRequestAsync(
        long userId,
        string password,
        string newEmail,
        string? code,
        CancellationToken ct = default
    )
    {
        var user = await _identityService.FindByIdAsync(userId);
        if (user is null)
            throw new AuthenticationException("Invalid credentials.");

        if (user.PasswordHash is null)
            throw new InvalidOperationException("Set a password first.");

        if (!await _identityService.CheckPasswordAsync(user, password))
            throw new AuthenticationException("Invalid credentials.");

        var existing = await _identityService.FindByEmailAsync(newEmail);
        if (existing is not null && existing.Id != userId)
            throw new ConflictException("Email already in use.");

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrEmpty(code))
            {
                await RequestStepUpCodeAsync(user, "change-email", ct);
                return true;
            }

            await ValidateStepUpCodeAsync(user.Id, "change-email", code, ct);
        }

        if (!await _emailCooldown.TryAcquireAsync("change-email", userId, ct))
            return false; // already sent recently

        var sent = await SendChangeEmailAsync(user, newEmail, ct);
        if (!sent)
            await _emailCooldown.ReleaseAsync("change-email", userId, ct);

        return false;
    }

    public async Task<bool> ConfirmEmailChangeAsync(
        string userId,
        string email,
        string token,
        CancellationToken ct = default
    )
    {
        if (!long.TryParse(userId, out var id))
            return false;

        var user = await _identityService.FindByIdAsync(id);
        if (user is null)
            return false;

        var (succeeded, _) = await _identityService.ChangeEmailAsync(user, email, token);
        return succeeded;
    }

    public async Task ChangeUsernameAsync(
        long userId,
        string password,
        string newUsername,
        CancellationToken ct = default
    )
    {
        var user = await _identityService.FindByIdAsync(userId);
        if (user is null)
            throw new AuthenticationException("Invalid credentials.");

        if (user.PasswordHash is null)
            throw new InvalidOperationException("Set a password first.");

        if (!await _identityService.CheckPasswordAsync(user, password))
            throw new AuthenticationException("Invalid credentials.");

        var existing = await _identityService.FindByNameAsync(newUsername);
        if (existing is not null && existing.Id != userId)
            throw new ConflictException("Username already taken.");

        var (succeeded, errors) = await _identityService.SetUserNameAsync(user, newUsername);
        if (!succeeded)
            throw new InvalidOperationException(string.Join(", ", errors));

        // Best-effort — the rename already succeeded; a broadcast failure just means other tabs
        // catch up on their next natural refetch (same philosophy as FileService's avatar fan-out).
        await BroadcastUsernameUpdatedAsync(userId, user.AvatarKey, newUsername, ct);
    }

    // --- Helpers ---

    private async Task<string> GenerateUniqueUsernameAsync(string email)
    {
        var localPart = email.Split('@')[0];
        var baseName = new string(localPart.Where(char.IsLetterOrDigit).ToArray());
        if (baseName.Length < 2)
            baseName = "user";
        if (baseName.Length > 28)
            baseName = baseName[..28];

        if (await _identityService.FindByNameAsync(baseName) is null)
            return baseName;

        for (var i = 1; i <= 9999; i++)
        {
            var candidate = $"{baseName}{i}";
            if (await _identityService.FindByNameAsync(candidate) is null)
                return candidate;
        }

        // Astronomically unlikely fallback — keeps the method total.
        return $"{baseName}{Guid.NewGuid().ToString("N")[..8]}";
    }

    private async Task<bool> SendVerificationEmailAsync(User user, CancellationToken ct)
    {
        try
        {
            var token = await _identityService.GenerateEmailConfirmationTokenAsync(user);
            var clientUrl = (_config["ClientUrl"] ?? "http://localhost:4200").TrimEnd('/');
            var link = $"{clientUrl}/verify-email?uid={user.Id}&token={WebUtility.UrlEncode(token)}";
            var (subject, html, text) = EmailTemplates.VerifyEmail(user.UserName!, link);
            await _emailSender.SendAsync(user.Email!, subject, html, text, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auth: failed to send verification email to user {UserId}", user.Id);
            return false;
        }
    }

    private async Task<bool> SendPasswordResetEmailAsync(User user, CancellationToken ct)
    {
        try
        {
            var token = await _identityService.GeneratePasswordResetTokenAsync(user);
            var clientUrl = (_config["ClientUrl"] ?? "http://localhost:4200").TrimEnd('/');
            var link = $"{clientUrl}/reset-password?uid={user.Id}&token={WebUtility.UrlEncode(token)}";
            var (subject, html, text) = EmailTemplates.ResetPassword(user.UserName!, link);
            await _emailSender.SendAsync(user.Email!, subject, html, text, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auth: failed to send password reset email to user {UserId}", user.Id);
            return false;
        }
    }

    /// <summary>Step-up gate (D20) for change-password/change-email on a 2FA-enabled account: mints
    /// a purpose-scoped code and emails it, cooldown-gated exactly like every other transactional
    /// send here. A no-op (not an error) when a code was already sent recently — the earlier one is
    /// still live.</summary>
    private async Task RequestStepUpCodeAsync(User user, string purpose, CancellationToken ct)
    {
        var cooldownPurpose = $"{purpose}-2fa";
        if (!await _emailCooldown.TryAcquireAsync(cooldownPurpose, user.Id, ct))
            return;

        var code = await _twoFactorStore.CreateStepUpCodeAsync(user.Id, purpose, ct);
        var sent = await SendStepUpCodeAsync(user, purpose, code, ct);
        if (!sent)
            await _emailCooldown.ReleaseAsync(cooldownPurpose, user.Id, ct);
    }

    /// <summary>Validates a step-up code for the given purpose, throwing InvalidOperationException
    /// with the same three-way message split as <see cref="Enable2faConfirmAsync"/> on failure.</summary>
    private async Task ValidateStepUpCodeAsync(long userId, string purpose, string code, CancellationToken ct)
    {
        var result = await _twoFactorStore.ValidateStepUpCodeAsync(userId, purpose, code, ct);
        if (result == TwoFactorValidationResult.Success)
            return;

        var message = result switch
        {
            TwoFactorValidationResult.TooManyAttempts => "Too many attempts. Please start over.",
            TwoFactorValidationResult.ExpiredOrUnknown => "Code expired. Please request a new one.",
            _ => "Invalid code.",
        };
        throw new InvalidOperationException(message);
    }

    private async Task<bool> SendStepUpCodeAsync(User user, string purpose, string code, CancellationToken ct)
    {
        try
        {
            var (subject, html, text) = purpose switch
            {
                "change-password" => EmailTemplates.ChangePasswordCode(user.UserName!, code),
                "change-email" => EmailTemplates.ChangeEmailCode(user.UserName!, code),
                _ => EmailTemplates.TwoFactorCode(user.UserName!, code),
            };
            await _emailSender.SendAsync(user.Email!, subject, html, text, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auth: failed to send {Purpose} step-up code to user {UserId}", purpose, user.Id);
            return false;
        }
    }

    private async Task<bool> SendChangeEmailAsync(User user, string newEmail, CancellationToken ct)
    {
        try
        {
            var token = await _identityService.GenerateChangeEmailTokenAsync(user, newEmail);
            var clientUrl = (_config["ClientUrl"] ?? "http://localhost:4200").TrimEnd('/');
            var link =
                $"{clientUrl}/confirm-email-change?uid={user.Id}&email={WebUtility.UrlEncode(newEmail)}&token={WebUtility.UrlEncode(token)}";
            var (subject, html, text) = EmailTemplates.ChangeEmail(user.UserName!, link);
            await _emailSender.SendAsync(newEmail, subject, html, text, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auth: failed to send change-email confirmation to user {UserId}", user.Id);
            return false;
        }
    }

    /// <summary>Fans a username change out to the surfaces that render it live — the same
    /// guilds + friends + own-tabs composition as FileService's avatar broadcast, duplicated here
    /// rather than shared (out of scope for this slice; see D19). Carries the user's CURRENT
    /// avatar key (not null) alongside the new username — the client applies AvatarKey
    /// unconditionally (null there means "no avatar", a real state), so this event must never
    /// claim "no avatar" for a user who has one just because this update didn't touch it.</summary>
    private async Task BroadcastUsernameUpdatedAsync(
        long userId,
        string? currentAvatarKey,
        string username,
        CancellationToken ct
    )
    {
        try
        {
            var payload = new ProfileUpdatedPayload(userId, currentAvatarKey, Username: username);

            var guildIds = await _guilds.GetGuildIdsForUserAsync(userId);
            foreach (var guildId in guildIds)
                await _broadcaster.BroadcastProfileUpdatedToGuildAsync(guildId, payload, ct);

            await _broadcaster.BroadcastProfileUpdatedToUserAsync(userId, payload, ct);
            var friendIds = await _friends.GetFriendIdsAsync(userId);
            foreach (var friendId in friendIds)
                await _broadcaster.BroadcastProfileUpdatedToUserAsync(friendId, payload, ct);
        }
        catch
        {
            // best-effort — see summary
        }
    }

    private async Task<bool> IsTrustedDeviceAsync(long userId, string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
            return false;

        var hash = _jwtService.HashRefreshToken(rawToken);
        var device = await _trustedDevices.GetValidAsync(userId, hash, ct);
        return device is not null;
    }

    private async Task<string> IssueTrustedDeviceAsync(long userId, CancellationToken ct)
    {
        var rawToken = _jwtService.GenerateRefreshToken();
        var device = new TrustedDevice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = _jwtService.HashRefreshToken(rawToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _trustedDevices.AddAsync(device, ct);
        await _trustedDevices.SaveChangesAsync(ct);
        return rawToken;
    }

    private async Task<bool> SendTwoFactorCodeAsync(User user, string code, CancellationToken ct)
    {
        try
        {
            var (subject, html, text) = EmailTemplates.TwoFactorCode(user.UserName!, code);
            await _emailSender.SendAsync(user.Email!, subject, html, text, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auth: failed to send 2FA code to user {UserId}", user.Id);
            return false;
        }
    }

    public async Task<(string accessToken, string rawRefreshToken)> IssueTokensAsync(
        User user,
        Guid? familyId = null
    )
    {
        var accessToken = _jwtService.GenerateAccessToken(user);
        var rawRefresh = _jwtService.GenerateRefreshToken();
        var refreshExpiry = int.Parse(_config["Jwt:RefreshTokenExpiryDays"] ?? "7");

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = _jwtService.HashRefreshToken(rawRefresh),
            FamilyId = familyId ?? Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(refreshExpiry),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _tokenRepository.AddAsync(refreshToken);
        await _tokenRepository.SaveChangesAsync();

        return (accessToken, rawRefresh);
    }

    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var tokens = await _tokenRepository.GetActiveFamilyTokensAsync(familyId, ct);

        foreach (var t in tokens)
            t.RevokedAt = DateTimeOffset.UtcNow;

        await _tokenRepository.SaveChangesAsync(ct);
    }

    private static UserResponse ToUserResponse(User user) =>
        new(
            user.Id,
            user.UserName!,
            user.Email!,
            user.AvatarKey,
            user.AccountStatus,
            user.EmailConfirmed,
            user.TwoFactorEnabled,
            user.PasswordHash != null
        );

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }
}
