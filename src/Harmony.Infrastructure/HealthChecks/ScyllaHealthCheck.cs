using Cassandra;
using Harmony.Infrastructure.Scylla;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harmony.Infrastructure.HealthChecks;

/// <summary>
/// Core dependency — messages live in Scylla, so a failed connection is reported Unhealthy. The
/// write-side circuit breaker (§5.66) reacts to sustained failures independently of this endpoint;
/// this just surfaces current reachability for ALB/ops visibility.
/// </summary>
public sealed class ScyllaHealthCheck(IScyllaSessionFactory sessionFactory) : IHealthCheck
{
    private static readonly SimpleStatement Probe = new(
        "SELECT release_version FROM system.local"
    );

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        try
        {
            await sessionFactory
                .Session.ExecuteAsync(Probe)
                .WaitAsync(TimeSpan.FromSeconds(3), ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("ScyllaDB probe query failed.", ex);
        }
    }
}
