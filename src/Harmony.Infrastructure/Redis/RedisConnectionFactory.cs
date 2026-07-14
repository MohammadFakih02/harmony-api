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

        // Fail FAST when Redis is down. The defaults (5s connect / 5s sync, 3 connect retries)
        // let a single send stack multiple 5s blocks during the down-detection window → the
        // "20–30s slow send with Redis down" symptom. Every Redis touch on the hot path already
        // fails OPEN (rate-limit, slowmode, dedup), so a tight ~1–2s ceiling per op just gets us
        // to that open path sooner. Only override when the connection string didn't set them.
        if (options.ConnectTimeout >= 5000)
            options.ConnectTimeout = 2000;
        if (options.SyncTimeout >= 5000)
            options.SyncTimeout = 1000;
        if (options.ConnectRetry > 1)
            options.ConnectRetry = 1;

        logger.LogInformation("Connecting to Redis at {Endpoints}", connectionString);

        var multiplexer = ConnectionMultiplexer.Connect(options);

        logger.LogInformation("Redis connection established.");

        return multiplexer;
    }
}
