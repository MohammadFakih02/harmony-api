using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddHarmonyRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // ----------------------------------------------------------------
            // Login limiter — keyed by IP address
            // 10 attempts per minute per IP. Hits auth endpoints hard before
            // Identity even checks the password.
            // ----------------------------------------------------------------
            options.AddFixedWindowLimiter("login", opt =>
            {
                opt.PermitLimit = 10;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 0; // no queuing — reject immediately
            });

            // ----------------------------------------------------------------
            // General API limiter — keyed by user ID if authenticated,
            // IP address if anonymous. 100 requests per 10 seconds.
            // ----------------------------------------------------------------
            options.AddPolicy("api", context =>
            {
                var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

                if (!string.IsNullOrEmpty(userId))
                {
                    // Authenticated — partition by user ID
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"user:{userId}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 100,
                            Window = TimeSpan.FromSeconds(10),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0
                        });
                }

                // Anonymous — partition by IP
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"anon:{ip}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromSeconds(10),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            });

            // Return 429 with a Retry-After header instead of the default empty response
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests. Please slow down." },
                    cancellationToken);
            };
        });

        return services;
    }
}