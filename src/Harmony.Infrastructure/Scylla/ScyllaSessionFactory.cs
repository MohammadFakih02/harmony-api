using Cassandra;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla;

public class ScyllaSessionFactory : IDisposable
{
    private readonly ISession _session;
    private readonly ILogger<ScyllaSessionFactory> _logger;
    private bool _disposed;

    public ScyllaSessionFactory(IConfiguration configuration, ILogger<ScyllaSessionFactory> logger)
    {
        _logger = logger;

        var contactPoints =
            configuration.GetSection("ScyllaDB:ContactPoints").Get<string[]>() ?? ["127.0.0.1"];

        var port = configuration.GetValue<int>("ScyllaDB:Port", 9042);
        var keyspace = configuration.GetValue<string>("ScyllaDB:Keyspace", "harmony");

        _logger.LogInformation(
            "Connecting to ScyllaDB at {ContactPoints}:{Port}, keyspace: {Keyspace}",
            string.Join(", ", contactPoints),
            port,
            keyspace
        );

        var cluster = Cluster
            .Builder()
            .AddContactPoints(contactPoints)
            .WithPort(port)
            .WithReconnectionPolicy(new ExponentialReconnectionPolicy(1000, 60000))
            .WithRetryPolicy(new DefaultRetryPolicy())
            .WithQueryOptions(
                new QueryOptions()
                    .SetConsistencyLevel(ConsistencyLevel.LocalQuorum)
                    .SetSerialConsistencyLevel(ConsistencyLevel.LocalSerial)
            )
            .Build();

        var session = cluster.Connect();
        session.Execute(
            $"CREATE KEYSPACE IF NOT EXISTS {keyspace} WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}"
        );
        session.ChangeKeyspace(keyspace);
        _session = session;

        _logger.LogInformation("ScyllaDB session established");
    }

    public ISession Session => _session;

    public void Dispose()
    {
        if (_disposed)
            return;
        _session.Dispose();
        _disposed = true;
    }
}
