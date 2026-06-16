using System.Security.Claims;
using Harmony.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Harmony.API.Filters;

/// <summary>
/// Per-user rate limiting for SignalR hub method invocations.
///
/// The ASP.NET Core rate-limiting middleware only sees the initial negotiate HTTP
/// request — subsequent WebSocket frames bypass middleware entirely. So hub methods
/// need their own limiter (NON-NEGOTIABLE #7: rate limit every hub method). This is a
/// Redis fixed-window counter, mirroring the documented <c>ratelimit:msg:{userId}</c>
/// key (§12) and reusing the same connection the dedup/unread features use.
///
/// Fails OPEN: if Redis is unavailable or errors, the invocation proceeds. Losing rate
/// limiting briefly is preferable to dropping legitimate real-time traffic — consistent
/// with the dedup/unread fail-open posture.
/// </summary>
public sealed class RateLimitHubFilter : IHubFilter
{
    private readonly IRedisConnectionProvider _redisProvider;
    private readonly ILogger<RateLimitHubFilter> _logger;

    public RateLimitHubFilter(
        IRedisConnectionProvider redisProvider,
        ILogger<RateLimitHubFilter> logger
    )
    {
        _redisProvider = redisProvider;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next
    )
    {
        var method = invocationContext.HubMethodName;
        var userId = ResolveUserId(invocationContext);

        if (userId is not null && await IsRateLimitedAsync(method, userId))
        {
            _logger.LogWarning(
                "Hub rate limit exceeded — user {UserId} on {Method}",
                userId,
                method
            );
            throw new HubException("Too many requests. Please slow down.");
        }

        return await next(invocationContext);
    }

    /// <summary>
    /// Fixed-window counter: INCR the per-(user, method) key; on the first hit set the
    /// window expiry; reject once the count exceeds the method's permit limit.
    /// </summary>
    private async Task<bool> IsRateLimitedAsync(string method, string userId)
    {
        if (!_redisProvider.IsConnected)
            return false; // fail open

        var (limit, window, key) = GetPolicy(method, userId);

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var count = await db.StringIncrementAsync(key);
            if (count == 1)
                await db.KeyExpireAsync(key, window);

            return count > limit;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hub rate limiter Redis error on {Method} — failing open", method);
            return false; // fail open
        }
    }

    /// <summary>
    /// Per-method limits. SendMessage gets the tight documented per-second window
    /// (<c>ratelimit:msg:{userId}</c>, §12); group/join churn and everything else gets a
    /// looser default. Tuned to be generous for normal use, restrictive against abuse.
    /// </summary>
    private static (int Limit, TimeSpan Window, string Key) GetPolicy(string method, string userId)
    {
        return method switch
        {
            "SendMessage" => (5, TimeSpan.FromSeconds(1), $"ratelimit:msg:{userId}"),
            _ => (20, TimeSpan.FromSeconds(10), $"ratelimit:hub:{method}:{userId}"),
        };
    }

    private static string? ResolveUserId(HubInvocationContext context)
    {
        var user = context.Context.User;
        return user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
    }
}
