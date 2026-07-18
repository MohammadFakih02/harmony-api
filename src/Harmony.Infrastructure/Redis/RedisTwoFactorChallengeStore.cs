using System.Security.Cryptography;
using System.Text;
using Harmony.Application.Exceptions;
using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis HASH-backed <see cref="ITwoFactorChallengeStore"/>. Fails CLOSED (D1) — every method
/// throws if Redis is unreachable, unlike every other Redis gate in the codebase. Codes are
/// plaintext in Redis (the attempt cap is the defense, same tradeoff as every OTP-over-email flow);
/// compared with <see cref="CryptographicOperations.FixedTimeEquals"/> to avoid timing leaks.
/// </summary>
public sealed class RedisTwoFactorChallengeStore : ITwoFactorChallengeStore
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly IRedisConnectionProvider _redisProvider;
    private readonly ILogger<RedisTwoFactorChallengeStore> _logger;

    public RedisTwoFactorChallengeStore(
        IRedisConnectionProvider redisProvider,
        ILogger<RedisTwoFactorChallengeStore> logger
    )
    {
        _redisProvider = redisProvider;
        _logger = logger;
    }

    public async Task<(string ChallengeToken, string Code)> CreateChallengeAsync(
        long userId,
        CancellationToken ct = default
    )
    {
        var db = RequireDatabase();
        var token = GenerateToken();
        var code = GenerateCode();

        try
        {
            var key = ChallengeKey(token);
            await db.HashSetAsync(
                key,
                new HashEntry[]
                {
                    new("userId", userId),
                    new("code", code),
                    new("attempts", 0),
                }
            );
            await db.KeyExpireAsync(key, Ttl);
            return (token, code);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TwoFactorChallengeStore: failed to create challenge for user {UserId}", userId);
            throw;
        }
    }

    public async Task<(TwoFactorValidationResult Result, long? UserId)> ValidateChallengeAsync(
        string challengeToken,
        string code,
        CancellationToken ct = default
    )
    {
        var db = RequireDatabase();

        try
        {
            var key = ChallengeKey(challengeToken);
            var entries = await db.HashGetAllAsync(key);
            if (entries.Length == 0)
                return (TwoFactorValidationResult.ExpiredOrUnknown, null);

            var map = entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            var userId = long.Parse(map["userId"]);
            var attempts = int.Parse(map["attempts"]);
            if (attempts >= MaxAttempts)
                return (TwoFactorValidationResult.TooManyAttempts, null);

            if (CodesMatch(map["code"], code))
            {
                await db.KeyDeleteAsync(key);
                return (TwoFactorValidationResult.Success, userId);
            }

            var newAttempts = await db.HashIncrementAsync(key, "attempts", 1);
            return newAttempts >= MaxAttempts
                ? (TwoFactorValidationResult.TooManyAttempts, null)
                : (TwoFactorValidationResult.InvalidCode, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TwoFactorChallengeStore: failed to validate challenge");
            throw;
        }
    }

    public async Task<(string Code, long UserId)?> RegenerateCodeAsync(
        string challengeToken,
        CancellationToken ct = default
    )
    {
        var db = RequireDatabase();

        try
        {
            var key = ChallengeKey(challengeToken);
            var userIdRaw = await db.HashGetAsync(key, "userId");
            if (userIdRaw.IsNullOrEmpty)
                return null;

            var newCode = GenerateCode();
            await db.HashSetAsync(
                key,
                new HashEntry[] { new("code", newCode), new("attempts", 0) }
            );
            await db.KeyExpireAsync(key, Ttl);
            return (newCode, long.Parse(userIdRaw.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TwoFactorChallengeStore: failed to regenerate challenge code");
            throw;
        }
    }

    public Task<string> CreateSetupCodeAsync(long userId, CancellationToken ct = default) =>
        CreateStepUpCodeAsync(userId, "setup", ct);

    public Task<TwoFactorValidationResult> ValidateSetupCodeAsync(
        long userId,
        string code,
        CancellationToken ct = default
    ) => ValidateStepUpCodeAsync(userId, "setup", code, ct);

    public async Task<string> CreateStepUpCodeAsync(
        long userId,
        string purpose,
        CancellationToken ct = default
    )
    {
        var db = RequireDatabase();
        var code = GenerateCode();

        try
        {
            var key = StepUpKey(purpose, userId);
            await db.HashSetAsync(
                key,
                new HashEntry[] { new("code", code), new("attempts", 0) }
            );
            await db.KeyExpireAsync(key, Ttl);
            return code;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TwoFactorChallengeStore: failed to create {Purpose} step-up code for user {UserId}", purpose, userId);
            throw;
        }
    }

    public async Task<TwoFactorValidationResult> ValidateStepUpCodeAsync(
        long userId,
        string purpose,
        string code,
        CancellationToken ct = default
    )
    {
        var db = RequireDatabase();

        try
        {
            var key = StepUpKey(purpose, userId);
            var entries = await db.HashGetAllAsync(key);
            if (entries.Length == 0)
                return TwoFactorValidationResult.ExpiredOrUnknown;

            var map = entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            var attempts = int.Parse(map["attempts"]);
            if (attempts >= MaxAttempts)
                return TwoFactorValidationResult.TooManyAttempts;

            if (CodesMatch(map["code"], code))
            {
                await db.KeyDeleteAsync(key);
                return TwoFactorValidationResult.Success;
            }

            var newAttempts = await db.HashIncrementAsync(key, "attempts", 1);
            return newAttempts >= MaxAttempts
                ? TwoFactorValidationResult.TooManyAttempts
                : TwoFactorValidationResult.InvalidCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TwoFactorChallengeStore: failed to validate {Purpose} step-up code for user {UserId}", purpose, userId);
            throw;
        }
    }

    private IDatabase RequireDatabase()
    {
        // Fail CLOSED (D1): unlike every cooldown/dedup gate in this codebase, a Redis outage here
        // must not let a 2FA-enabled account log in (or get 2FA disabled) unchallenged. 503, not a
        // 4xx — this is an infra outage, not something the caller did wrong.
        if (!_redisProvider.IsConnected)
            throw new ServiceUnavailableException("Two-factor authentication is temporarily unavailable.");

        return _redisProvider.Connection!.GetDatabase();
    }

    private static bool CodesMatch(string stored, string submitted)
    {
        var storedBytes = Encoding.UTF8.GetBytes(stored);
        var submittedBytes = Encoding.UTF8.GetBytes(submitted);
        return storedBytes.Length == submittedBytes.Length
            && CryptographicOperations.FixedTimeEquals(storedBytes, submittedBytes);
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string GenerateCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string ChallengeKey(string token) => $"2fa:challenge:{token}";

    // Purpose "setup" reproduces the pre-existing "2fa:setup:{userId}" key exactly, so this
    // generalization doesn't change the key format for the already-live enable-2FA flow.
    private static string StepUpKey(string purpose, long userId) => $"2fa:{purpose}:{userId}";
}
