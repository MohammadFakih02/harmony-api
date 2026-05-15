using Cassandra;
using Harmony.Infrastructure.Scylla;

namespace Harmony.IntegrationTests.Infrastructure;

public class ScyllaSessionFactoryStub : IScyllaSessionFactory
{
    private readonly ISession _session;

    public ScyllaSessionFactoryStub(ISession session, string keyspace = "harmony_test")
    {
        _session = session;
        Keyspace = keyspace;
    }

    public ISession Session => _session;
    public string Keyspace { get; }
}