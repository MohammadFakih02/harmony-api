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
            // Login limiter — Partitioned by IP address
            // 10 attempts per minute per IP. Hits auth endpoints hard before
            // Identity even checks the password.
            // ----------------------------------------------------------------
            options.AddPolicy(
                "login",
                context =>
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"login:{ip}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0, // reject immediately
                        }
                    );
                }
            );

            // ----------------------------------------------------------------
            // General API limiter — keyed by user ID if authenticated,
            // IP address if anonymous. Reads and writes use separate buckets
            // so a page-load burst of GETs never eats into the write quota.
            // ----------------------------------------------------------------
            options.AddPolicy(
                "api",
                context =>
                {
                    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

                    if (!string.IsNullOrEmpty(userId))
                    {
                        if (context.Request.Method != HttpMethods.Get)
                        {
                            // Writes — strict. Catches message floods / spam without
                            // ever tripping on normal interactive use (6 writes/s average).
                            return RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: $"user:w:{userId}",
                                factory: _ => new FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = 90,
                                    Window = TimeSpan.FromSeconds(10),
                                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                                    QueueLimit = 0,
                                }
                            );
                        }

                        // Reads — generous. A deep-link page load fans out ~20-25 GETs;
                        // 500/10s absorbs 20 rapid back-to-back refreshes without a 429.
                        return RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: $"user:r:{userId}",
                            factory: _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 500,
                                Window = TimeSpan.FromSeconds(10),
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                                QueueLimit = 0,
                            }
                        );
                    }

                    // Anonymous — partition by IP, same limit regardless of method
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"anon:{ip}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 20,
                            Window = TimeSpan.FromSeconds(10),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0,
                        }
                    );
                }
            );

            // ----------------------------------------------------------------
            // Public asset limiter — avatars/banners are fetched by bare <img>
            // tags (no auth header → always anonymous), and one member list can
            // burst dozens of them. IP-partitioned, generous; the endpoint is
            // cheap (one indexed row read + a local HMAC presign, no MinIO IO).
            // ----------------------------------------------------------------
            options.AddPolicy(
                "assets",
                context =>
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"assets:{ip}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 300,
                            Window = TimeSpan.FromSeconds(10),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0,
                        }
                    );
                }
            );

            // Return 429 with a Retry-After header instead of the default empty response
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = (
                        (int)retryAfter.TotalSeconds
                    ).ToString();

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests. Please slow down." },
                    cancellationToken
                );
            };
        });

        return services;
    }
}
