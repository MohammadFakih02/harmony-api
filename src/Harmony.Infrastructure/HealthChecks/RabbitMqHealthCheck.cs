using Harmony.Infrastructure.RabbitMQ;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harmony.Infrastructure.HealthChecks;

/// <summary>
/// Core dependency — every send/edit/delete goes through RabbitMQ, so a failed connection is
/// reported Unhealthy. GetConnectionAsync self-heals (reconnects on demand, never caches a failed
/// attempt — see RabbitMQConnection's own doc comment), so this check doubles as a nudge to retry.
/// </summary>
public sealed class RabbitMqHealthCheck(RabbitMQConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        try
        {
            var conn = await connection.GetConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);
            return conn.IsOpen
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("RabbitMQ connection is not open.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ connection failed.", ex);
        }
    }
}
