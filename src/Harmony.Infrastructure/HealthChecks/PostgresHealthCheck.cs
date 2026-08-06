using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harmony.Infrastructure.HealthChecks;

/// <summary>
/// Core dependency — most requests touch Postgres, so a failed connection is reported
/// Unhealthy (503), pulling the task out of ALB rotation rather than just flagging Degraded.
/// </summary>
public sealed class PostgresHealthCheck(HarmonyDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("CanConnectAsync returned false.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres connection failed.", ex);
        }
    }
}
