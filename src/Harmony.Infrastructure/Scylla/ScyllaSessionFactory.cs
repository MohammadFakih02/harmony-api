using System;
using System.Linq; // Ensure Linq is imported
using System.Net;
using Cassandra;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla;

public class ScyllaSessionFactory : IScyllaSessionFactory, IDisposable
{
    private readonly ICluster _cluster;
    private readonly ILogger<ScyllaSessionFactory> _logger;
    private readonly string _keyspace;
    private readonly object _sessionLock = new();
    private ISession? _session;
    private bool _disposed;

    public ScyllaSessionFactory(IConfiguration configuration, ILogger<ScyllaSessionFactory> logger)
    {
        _logger = logger;
        _keyspace = configuration.GetValue<string>("ScyllaDB:Keyspace", "harmony")!;

        var contactPoints =
            configuration.GetSection("ScyllaDB:ContactPoints").Get<string[]>() ?? ["127.0.0.1"];

        var port = configuration.GetValue<int>("ScyllaDB:Port", 9042);

        // Robust environment resolution
        var env =
            configuration["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        var isLocal =
            env.Equals("Development", StringComparison.OrdinalIgnoreCase)
            || env.Equals("Test", StringComparison.OrdinalIgnoreCase)
            || AppDomain
                .CurrentDomain.GetAssemblies()
                .Any(a => a.FullName!.Contains("xunit", StringComparison.OrdinalIgnoreCase));

        var clusterBuilder = Cluster
            .Builder()
            .AddContactPoints(contactPoints)
            .WithPort(port)
            .WithLoadBalancingPolicy(Policies.DefaultLoadBalancingPolicy)
            .WithReconnectionPolicy(new ConstantReconnectionPolicy(2000))
            .WithPoolingOptions(new PoolingOptions().SetHeartBeatInterval(5000))
            .WithQueryOptions(
                new QueryOptions()
                    .SetConsistencyLevel(ConsistencyLevel.LocalQuorum)
                    .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial)
                    .SetPrepareOnAllHosts(true)
                    .SetReprepareOnUp(true)
            )
            .WithExecutionProfiles(options =>
                options
                    .WithProfile(
                        "default",
                        profile =>
                            profile
                                .WithLoadBalancingPolicy(Policies.DefaultLoadBalancingPolicy)
                                .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
                                .WithSerialConsistencyLevel(ConsistencyLevel.LocalSerial)
                                .WithRetryPolicy(new LoggingRetryPolicy(new DefaultRetryPolicy()))
                    )
                    .WithProfile(
                        "write",
                        profile =>
                            profile
                                .WithLoadBalancingPolicy(Policies.DefaultLoadBalancingPolicy)
                                .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
                                .WithSerialConsistencyLevel(ConsistencyLevel.LocalSerial)
                                .WithRetryPolicy(new LoggingRetryPolicy(new DefaultRetryPolicy()))
                    )
                    .WithProfile(
                        "read",
                        profile =>
                            profile
                                .WithLoadBalancingPolicy(Policies.DefaultLoadBalancingPolicy)
                                .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
                                .WithRetryPolicy(new LoggingRetryPolicy(new DefaultRetryPolicy()))
                                .WithSpeculativeExecutionPolicy(
                                    new ConstantSpeculativeExecutionPolicy(200, 1)
                                )
                    )
                    .WithProfile(
                        "read-states",
                        profile =>
                            profile
                                .WithLoadBalancingPolicy(Policies.DefaultLoadBalancingPolicy)
                                .WithConsistencyLevel(ConsistencyLevel.LocalOne)
                                .WithRetryPolicy(new LoggingRetryPolicy(new DefaultRetryPolicy()))
                                .WithSpeculativeExecutionPolicy(
                                    new ConstantSpeculativeExecutionPolicy(100, 1)
                                )
                    )
            );

        if (isLocal)
        {
            clusterBuilder.WithAddressTranslator(new LocalhostAddressTranslator());
            _logger.LogInformation(
                "ScyllaDB LocalhostAddressTranslator enabled (Docker workaround)."
            );
        }

        _cluster = clusterBuilder.Build();
    }

    /// <summary>
    /// The shared Scylla session, established on first access. Deliberately NOT a
    /// <c>Lazy&lt;ISession&gt;</c>: a Lazy caches the exception forever if the first <c>Connect()</c>
    /// throws (Scylla still booting, ~60–90s, or a restart) — every later query would rethrow the
    /// stale failure until the process restarts. This double-checked getter caches the session only
    /// on success and lets the next caller retry. Once established, the driver's reconnection policy
    /// handles node recovery on the live session.
    /// </summary>
    public ISession Session
    {
        get
        {
            var existing = _session;
            if (existing is not null)
                return existing;

            lock (_sessionLock)
            {
                if (_session is not null)
                    return _session;

                _logger.LogInformation("Establishing ScyllaDB session...");
                var session = _cluster.Connect();
                session.Execute(
                    $"CREATE KEYSPACE IF NOT EXISTS {_keyspace} WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}"
                );
                session.ChangeKeyspace(_keyspace);
                _logger.LogInformation("ScyllaDB session established.");
                _session = session;
                return session;
            }
        }
    }

    public string Keyspace => _keyspace;

    public void Dispose()
    {
        if (_disposed)
            return;

        _session?.Dispose();
        _cluster.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Custom address translator to resolve Docker container subnets back to localhost.
/// </summary>
public class LocalhostAddressTranslator : IAddressTranslator
{
    public IPEndPoint Translate(IPEndPoint address)
    {
        // Map all discovered cluster peer IPs back to 127.0.0.1, preserving the port.
        // This forces localhost routing for host-to-container connections.
        return new IPEndPoint(IPAddress.Loopback, address.Port);
    }
}
