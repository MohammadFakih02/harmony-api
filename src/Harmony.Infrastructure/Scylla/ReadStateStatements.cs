using Cassandra;

namespace Harmony.Infrastructure.Scylla;

public class ReadStateStatements
{
    public PreparedStatement Upsert { get; }
    public PreparedStatement SelectOne { get; }

    public ReadStateStatements(IScyllaSessionFactory factory)
    {
        var session = factory.Session;
        var ks = factory.Keyspace;

        Upsert = session.Prepare(
            $@"INSERT INTO {ks}.read_states (user_id, channel_id, last_read_message_id)
               VALUES (?, ?, ?)"
        );

        SelectOne = session.Prepare(
            $@"SELECT last_read_message_id
               FROM {ks}.read_states
               WHERE user_id = ? AND channel_id = ?"
        );
    }
}
