using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="ISlowmodeGate"/> — an atomic <c>SET key "1" NX EX slowmodeSeconds</c>
/// per (channel, user): the first send claims the key and is allowed; further sends are rejected
/// until the TTL lapses. Fails OPEN (send allowed) when Redis is unavailable, matching every other
/// Redis gate in the codebase — slowmode briefly not enforced beats messages being dropped.
/// </summary>
public sealed class RedisSlowmodeGate : ISlowmodeGate
{
    private readonly IRedisConnectionProvider _redisProvider;
    private readonly ILogger<RedisSlowmodeGate> _logger;

    public RedisSlowmodeGate(IRedisConnectionProvider redisProvider, ILogger<RedisSlowmodeGate> logger)
    {
        _redisProvider = redisProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> TryConsumeAsync(
        long channelId,
        long userId,
        int slowmodeSeconds,
        CancellationToken ct = default
    )
    {
        if (slowmodeSeconds <= 0)
            return true;

        if (!_redisProvider.IsConnected)
            return true; // fail open

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var key = $"slowmode:{channelId}:{userId}";

            // true  → key was set   → no cooldown was running → send allowed (slot consumed)
            // false → key existed   → cooldown still running   → send rejected
            return await db.StringSetAsync(
                key, "1", TimeSpan.FromSeconds(slowmodeSeconds), When.NotExists, CommandFlags.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SlowmodeGate: Redis error for channel {ChannelId} user {UserId} — failing open",
                channelId, userId);
            return true;
        }
    }
}
