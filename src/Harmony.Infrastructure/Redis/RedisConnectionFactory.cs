using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Creates and owns the singleton <see cref="IConnectionMultiplexer"/> used by
/// both the SignalR Redis backplane and <see cref="RedisMessageDeduplicator"/>.
///
/// StackExchange.Redis is explicitly designed for singleton use — one multiplexer
/// per process, shared across all consumers. Creating multiple multiplexers wastes
/// connections and defeats connection pooling.
///
/// The <c>abortConnect=false</c> flag in the connection string is critical:
/// without it a Redis restart during a rolling deploy throws on startup and
/// kills the pod. With it the multiplexer retries in the background.
/// </summary>
public static class RedisConnectionFactory
{
    /// <summary>
    /// Creates a configured <see cref="IConnectionMultiplexer"/> from the
    /// ConnectionStrings:Redis configuration value.
    ///
    /// Returns null when the connection string is absent or empty —
    /// callers must handle the null case and skip Redis-dependent features.
    /// </summary>
    public static IConnectionMultiplexer? CreateMultiplexer(
        IConfiguration configuration,
        ILogger logger
    )
    {
        var connectionString = configuration.GetConnectionString("Redis");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogInformation(
                "Redis connection string is empty — deduplication and backplane disabled."
            );
            return null;
        }

        var options = ConfigurationOptions.Parse(connectionString);

        // Never throw on connect — retry silently in background.
        // This keeps the pod alive if Redis is briefly unreachable at startup.
        options.AbortOnConnectFail = false;

        logger.LogInformation("Connecting to Redis at {Endpoints}", connectionString);

        var multiplexer = ConnectionMultiplexer.Connect(options);

        logger.LogInformation("Redis connection established.");

        return multiplexer;
    }
}
