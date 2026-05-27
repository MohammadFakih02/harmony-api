using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Harmony.Infrastructure.Resilience;

public static class ResiliencePolicies
{
    // RabbitMQ — circuit breaker only, no retry
    // Rationale: retrying a publish on the same down broker wastes time.
    // The circuit opens fast and fails immediately until RabbitMQ recovers.
    public static AsyncCircuitBreakerPolicy RabbitMQCircuitBreaker(ILogger logger) =>
        Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, duration) =>
                    logger.LogWarning(
                        "RabbitMQ circuit breaker OPEN for {Duration}s — {Message}",
                        duration.TotalSeconds,
                        ex.Message
                    ),
                onReset: () =>
                    logger.LogInformation("RabbitMQ circuit breaker CLOSED — connection restored"),
                onHalfOpen: () =>
                    logger.LogInformation("RabbitMQ circuit breaker HALF-OPEN — testing connection")
            );

    // ScyllaDB — retry with exponential backoff, then circuit breaker
    // Rationale: Scylla blips are often transient (GC pause, leader election).
    // Retry 3 times before giving up, circuit opens after repeated failures.
    public static AsyncPolicy ScyllaRetryPolicy(ILogger logger) =>
        Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
                onRetry: (ex, delay, attempt, _) =>
                    logger.LogWarning(
                        "ScyllaDB retry {Attempt}/3 after {Delay}ms — {Message}",
                        attempt,
                        delay.TotalMilliseconds,
                        ex.Message
                    )
            );

    public static AsyncCircuitBreakerPolicy ScyllaCircuitBreaker(ILogger logger) =>
        Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(20),
                onBreak: (ex, duration) =>
                    logger.LogWarning(
                        "ScyllaDB circuit breaker OPEN for {Duration}s — {Message}",
                        duration.TotalSeconds,
                        ex.Message
                    ),
                onReset: () =>
                    logger.LogInformation("ScyllaDB circuit breaker CLOSED — connection restored"),
                onHalfOpen: () =>
                    logger.LogInformation("ScyllaDB circuit breaker HALF-OPEN — testing connection")
            );

    public static AsyncPolicy ScyllaPolicy(ILogger logger) =>
        Policy.WrapAsync(ScyllaCircuitBreaker(logger), ScyllaRetryPolicy(logger));
}
