using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="IEmailCooldownGate"/> — an atomic <c>SET key "1" NX EX 60</c> per
/// (purpose, user): the first send claims the key and is allowed; further sends are rejected
/// until the TTL lapses. Fails OPEN (send allowed) when Redis is unavailable, matching every
/// other Redis gate in the codebase (RedisSlowmodeGate) — a missed cooldown beats a user unable
/// to request a verification email at all.
/// </summary>
public sealed class RedisEmailCooldownGate : IEmailCooldownGate
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private readonly IRedisConnectionProvider _redisProvider;
    private readonly ILogger<RedisEmailCooldownGate> _logger;

    public RedisEmailCooldownGate(
        IRedisConnectionProvider redisProvider,
        ILogger<RedisEmailCooldownGate> logger
    )
    {
        _redisProvider = redisProvider;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(string purpose, long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return true; // fail open

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var key = $"email:cooldown:{purpose}:{userId}";
            return await db.StringSetAsync(key, "1", Cooldown, When.NotExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "EmailCooldownGate: Redis error for purpose {Purpose} user {UserId} — failing open",
                purpose,
                userId
            );
            return true;
        }
    }

    public async Task ReleaseAsync(string purpose, long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            await db.KeyDeleteAsync($"email:cooldown:{purpose}:{userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "EmailCooldownGate: Redis error releasing purpose {Purpose} user {UserId}",
                purpose,
                userId
            );
        }
    }
}
