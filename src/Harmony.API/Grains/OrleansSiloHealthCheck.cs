using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// Surfaces the co-hosted Orleans silo on <c>/health</c> (Track D0): resolves the grain factory and
/// round-trips a call through <see cref="IPingGrain"/> with a short timeout. If the silo can't activate
/// a grain (silo not started, scheduler wedged), the check reports Unhealthy and <c>/health</c> flips to
/// 503 — the same contract the Postgres/Scylla/RabbitMQ checks use. Registered only outside the Test
/// environment, where no silo is co-hosted (see Program.cs).
/// </summary>
public sealed class OrleansSiloHealthCheck(IGrainFactory grains) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pong = await grains
                .GetGrain<IPingGrain>("health")
                .Ping()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return HealthCheckResult.Healthy(pong);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Orleans silo did not respond", ex);
        }
    }
}
