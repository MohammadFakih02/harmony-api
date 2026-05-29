using Cassandra;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla;

public class ScyllaSessionFactory : IScyllaSessionFactory, IDisposable
{
    private readonly ISession _session;
    private readonly ILogger<ScyllaSessionFactory> _logger;
    private bool _disposed;

    private readonly string _keyspace;

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

        var cluster = Cluster
            .Builder()
            .AddContactPoints(contactPoints)
            .WithPort(port)
            .WithLoadBalancingPolicy(Policies.DefaultLoadBalancingPolicy)
            .WithReconnectionPolicy(new ExponentialReconnectionPolicy(1000, 60000))
            .WithRetryPolicy(new LoggingRetryPolicy(new DefaultRetryPolicy()))
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
            )
            .Build();

        var session = cluster.Connect();
        session.Execute(
            $"CREATE KEYSPACE IF NOT EXISTS {_keyspace} WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}"
        );
        session.ChangeKeyspace(_keyspace);
        _session = session;

        _logger.LogInformation("ScyllaDB session established (shard-aware)");
    }

    public ISession Session => _session;
    public string Keyspace => _keyspace;

    public void Dispose()
    {
        if (_disposed)
            return;
        _session.Dispose();
        _disposed = true;
    }
}
