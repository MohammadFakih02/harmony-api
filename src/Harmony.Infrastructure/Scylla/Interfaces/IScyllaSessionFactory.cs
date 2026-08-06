using Cassandra;

namespace Harmony.Infrastructure.Scylla;

public interface IScyllaSessionFactory
{
    ISession Session { get; }
    string Keyspace { get; }
}
