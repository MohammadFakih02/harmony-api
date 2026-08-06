using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harmony.Infrastructure.HealthChecks;

/// <summary>
/// Redis backs presence, unread-count cache, dedup and rate limiting — every one of those paths is
/// already designed to fail open when Redis is unavailable (NON-NEGOTIABLE-adjacent convention
/// followed throughout this codebase, e.g. the cooldown gates). So a down Redis is reported
/// Degraded, not Unhealthy — the app keeps serving, just with those niceties off.
/// </summary>
public sealed class RedisHealthCheck(IRedisConnectionProvider redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        if (redis.Connection is null || !redis.IsConnected)
        {
            return HealthCheckResult.Degraded("Redis multiplexer is not connected.");
        }

        try
        {
            await redis.Connection.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Redis PING failed.", ex);
        }
    }
}
