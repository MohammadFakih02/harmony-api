using Harmony.Infrastructure.RabbitMQ;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.HealthChecks;

/// <summary>
/// §18's "nothing watches the DLQ" gap. A non-empty DLQ means a human needs to look — the genuine
/// prod paths that land here (class-23 constraint poison, the consumers' catch-all) are things
/// retry logic already gave up on, not something this check itself should try to fix. Reported
/// Degraded (not Unhealthy): the app is fully functional with messages sitting in the DLQ, so this
/// should surface for ops, not pull the task out of ALB rotation.
/// </summary>
public sealed class DeadLetterQueueHealthCheck(
    RabbitMQConnection connection,
    ILogger<DeadLetterQueueHealthCheck> logger
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        try
        {
            await using var channel = await connection
                .CreateChannelAsync()
                .WaitAsync(TimeSpan.FromSeconds(3), ct);
            var result = await channel
                .QueueDeclarePassiveAsync(Topology.DeadLetterQueue, ct)
                .WaitAsync(TimeSpan.FromSeconds(3), ct);

            var depth = result.MessageCount;
            var data = new Dictionary<string, object> { ["depth"] = depth };

            if (depth == 0)
            {
                return HealthCheckResult.Healthy(data: data);
            }

            logger.LogWarning(
                "Dead-letter queue depth is {Depth} — a human needs to look (§18).",
                depth
            );
            return HealthCheckResult.Degraded(
                $"{depth} message(s) on the dead-letter queue.",
                data: data
            );
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Could not read dead-letter queue depth.", ex);
        }
    }
}
