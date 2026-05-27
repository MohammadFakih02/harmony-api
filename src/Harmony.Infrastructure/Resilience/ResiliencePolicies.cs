using Cassandra;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using RabbitMQ.Client.Exceptions;

namespace Harmony.Infrastructure.Resilience;

public static class ResiliencePolicies
{
    // RabbitMQ — circuit breaker only on real connection failures
    // No retry — if publish fails, fail fast and let the circuit track it
    public static AsyncCircuitBreakerPolicy RabbitMQCircuitBreaker(ILogger logger) =>
        Policy
            .Handle<RabbitMQClientException>()
            .Or<System.Net.Sockets.SocketException>()
            .Or<System.IO.IOException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(10),
                onBreak: (ex, duration) =>
                    logger.LogWarning(
                        "RabbitMQ circuit breaker OPEN for {Duration}s — {Message}",
                        duration.TotalSeconds,
                        ex.Message
                    ),
                onReset: () =>
                    logger.LogInformation("RabbitMQ circuit breaker CLOSED — connection restored"),
                onHalfOpen: () =>
                    logger.LogInformation("RabbitMQ circuit breaker HALF-OPEN — testing")
            );

    // ScyllaDB — circuit breaker ONLY
    // The ScyllaDB driver handles retries internally via IdempotenceAwareRetryPolicy
    // and automatic failover. Adding Polly retries on top causes double-retrying
    // and unnecessary load on a struggling cluster.
    // The circuit breaker catches total cluster failures after the driver gives up.
    public static AsyncCircuitBreakerPolicy ScyllaCircuitBreaker(ILogger logger) =>
        Policy
            .Handle<DriverException>()
            .Or<NoHostAvailableException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, duration) =>
                    logger.LogWarning(
                        "ScyllaDB circuit breaker OPEN for {Duration}s — {Message}",
                        duration.TotalSeconds,
                        ex.Message
                    ),
                onReset: () =>
                    logger.LogInformation("ScyllaDB circuit breaker CLOSED — connection restored"),
                onHalfOpen: () =>
                    logger.LogInformation("ScyllaDB circuit breaker HALF-OPEN — testing")
            );

    // ScyllaPolicy is now just the circuit breaker — no Polly retry wrapper
    public static AsyncPolicy ScyllaPolicy(ILogger logger) => ScyllaCircuitBreaker(logger);
}

// Singleton providers — share circuit breaker state across all scoped instances
public class ScyllaPolicyProvider
{
    public IAsyncPolicy Policy { get; }

    public ScyllaPolicyProvider(ILogger<ScyllaPolicyProvider> logger)
    {
        Policy = ResiliencePolicies.ScyllaPolicy(logger);
    }
}

public class RabbitMQPolicyProvider
{
    public IAsyncPolicy Policy { get; }

    public RabbitMQPolicyProvider(ILogger<RabbitMQPolicyProvider> logger)
    {
        Policy = ResiliencePolicies.RabbitMQCircuitBreaker(logger);
    }
}
