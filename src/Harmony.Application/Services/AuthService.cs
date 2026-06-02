using System.Security.Authentication;
using Harmony.Domain.Domain.Entities;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Harmony.Application.Services;
using Microsoft.Extensions.Configuration;

namespace Harmony.Application.Services;

public class AuthService : IAuthService
{
    private readonly IIdentityService _identityService;
    private readonly IRefreshTokenRepository _tokenRepository;
    private readonly IJwtService _jwtService;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IConfiguration _config;

    public AuthService(
        IIdentityService identityService,
        IRefreshTokenRepository tokenRepository,
        IJwtService jwtService,
        ISnowflakeIdGenerator snowflake,
        IConfiguration _config
    )
    {
        _identityService = identityService;
        _tokenRepository = tokenRepository;
        _jwtService = jwtService;
        _snowflake = snowflake;
        this._config = _config;
    }

    public async Task<(AuthResponse response, string rawRefreshToken)> RegisterAsync(
        RegisterRequest request,
        CancellationToken ct = default
    )
    {
        if (!IsValidEmail(request.Email))
            throw new ArgumentException("Invalid email format.");

        if (await _identityService.FindByEmailAsync(request.Email) is not null)
            throw new InvalidOperationException("Email already in use.");

        if (await _identityService.FindByNameAsync(request.Username) is not null)
            throw new InvalidOperationException("Username already taken.");

        var user = new User
        {
            Id = _snowflake.NextId(),
            UserName = request.Username,
            Email = request.Email,
            Discriminator = GenerateDiscriminator(),
            AccountStatus = "active",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var (succeeded, errors) = await _identityService.CreateUserAsync(user, request.Password);
        if (!succeeded)
            throw new InvalidOperationException(string.Join(", ", errors));

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user);
        return (new AuthResponse(accessToken, ToUserResponse(user)), rawRefreshToken);
    }

    public async Task<(AuthResponse response, string rawRefreshToken)> LoginAsync(
        LoginRequest request,
        CancellationToken ct = default
    )
    {
        var user = await _identityService.FindByEmailAsync(request.Email);
        if (user is null || !await _identityService.CheckPasswordAsync(user, request.Password))
            throw new AuthenticationException("Invalid email or password.");

        if (user.AccountStatus != "active")
            throw new AuthenticationException("Account is not active.");

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user);
        return (new AuthResponse(accessToken, ToUserResponse(user)), rawRefreshToken);
    }

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
            await RevokeFamilyAsync(stored.FamilyId, ct);
            throw new AuthenticationException("Refresh token reuse detected. Please log in again.");
        }

        if (stored.ExpiresAt < DateTimeOffset.UtcNow)
        {
            stored.RevokedAt = DateTimeOffset.UtcNow;
            await _tokenRepository.SaveChangesAsync(ct);
            throw new AuthenticationException("Refresh token expired.");
        }

        stored.RevokedAt = DateTimeOffset.UtcNow;
        await _tokenRepository.SaveChangesAsync(ct);

        var (accessToken, newRawRefreshToken) = await IssueTokensAsync(
            stored.User,
            stored.FamilyId
        );
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

    // --- Helpers ---

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

    private static string GenerateDiscriminator() => Random.Shared.Next(0, 10000).ToString("D4");

    private static UserResponse ToUserResponse(User user) =>
        new(
            user.Id,
            user.UserName!,
            user.Discriminator,
            user.Email!,
            user.AvatarKey,
            user.AccountStatus
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
