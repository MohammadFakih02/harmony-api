using Cassandra;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla;

public class ScyllaSessionFactory : IScyllaSessionFactory, IDisposable
{
    private readonly ICluster _cluster;
    private readonly ISession _session;
    private readonly ILogger<ScyllaSessionFactory> _logger;
    private readonly string _keyspace;
    private bool _disposed;

    public ScyllaSessionFactory(IConfiguration configuration, ILogger<ScyllaSessionFactory> logger)
    {
        _logger = logger;

        _keyspace = configuration.GetValue<string>("ScyllaDB:Keyspace", "harmony")!;

        var contactPoints =
            configuration.GetSection("ScyllaDB:ContactPoints").Get<string[]>() ?? ["127.0.0.1"];

        var port = configuration.GetValue<int>("ScyllaDB:Port", 9042);

        _logger.LogInformation(
            "Connecting to ScyllaDB at {ContactPoints}:{Port}, keyspace: {Keyspace}",
            string.Join(", ", contactPoints),
            port,
            _keyspace
        );

        _cluster = Cluster
            .Builder()
            .AddContactPoints(contactPoints)
            .WithPort(port)
            // Token-aware + DC-aware load balancing
            // Routes queries directly to the replica that owns the data
            .WithLoadBalancingPolicy(Policies.NewDefaultLoadBalancingPolicy("datacenter1"))
            // Exponential reconnection: starts at 1s, caps at 60s
            // Driver reconnects automatically in the background — no manual intervention needed
            .WithReconnectionPolicy(new ConstantReconnectionPolicy(2000))
            // IdempotenceAwareRetryPolicy wraps DefaultRetryPolicy:
            // - Only retries idempotent queries (reads) on timeout
            // - Never retries non-idempotent queries (writes) to prevent double-writes
            // LoggingRetryPolicy wraps that to log every retry decision
            .WithRetryPolicy(
                new IdempotenceAwareRetryPolicy(new LoggingRetryPolicy(new DefaultRetryPolicy()))
            )
            // Speculative execution: if a node takes >500ms to respond on a read,
            // fire the same query at a second node and use whichever replies first
            // Only fires for queries marked SetIdempotence(true)
            .WithSpeculativeExecutionPolicy(
                new ConstantSpeculativeExecutionPolicy(delay: 500, maxSpeculativeExecutions: 1)
            )
            .WithQueryOptions(
                new QueryOptions()
                    .SetConsistencyLevel(ConsistencyLevel.LocalQuorum)
                    .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial)
                    // Writes are NOT idempotent by default — must opt-in per statement
                    .SetDefaultIdempotence(false)
            )
            .Build();

        // Bootstrap: connect without keyspace, create it, then switch
        var bootstrapSession = _cluster.Connect();
        bootstrapSession.Execute(
            $"CREATE KEYSPACE IF NOT EXISTS {_keyspace} "
                + $"WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}"
        );
        bootstrapSession.Dispose();

        _session = _cluster.Connect(_keyspace);

        _logger.LogInformation("ScyllaDB session established");
    }

    public ISession Session => _session;
    public string Keyspace => _keyspace;

    public void Dispose()
    {
        if (_disposed)
            return;
        // Shutdown closes all sessions and background threads cleanly
        _cluster.Shutdown();
        _disposed = true;
    }
}
