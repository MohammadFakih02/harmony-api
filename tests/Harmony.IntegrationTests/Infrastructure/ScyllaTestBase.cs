using Cassandra;

namespace Harmony.IntegrationTests.Infrastructure;

public abstract class ScyllaTestBase : IAsyncLifetime
{
    private ICluster? _cluster;
    protected ISession Session { get; private set; } = null!;

    protected abstract IEnumerable<string> TablesToTruncate { get; }

    public virtual async Task InitializeAsync()
    {
        // Tests use simpler cluster config — no speculative execution or DC-aware policy
        // since we're hitting a single local node
        _cluster = Cluster
            .Builder()
            .AddContactPoint("127.0.0.1")
            .WithPort(9042)
            .WithRetryPolicy(new DefaultRetryPolicy())
            .WithReconnectionPolicy(new ConstantReconnectionPolicy(2000))
            .Build();

        var bootstrapSession = _cluster.Connect();
        bootstrapSession.Execute(
            @"
            CREATE KEYSPACE IF NOT EXISTS harmony_test
            WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}"
        );
        bootstrapSession.Dispose();

        Session = _cluster.Connect("harmony_test");

        await CreateTablesAsync();
        await TruncateTablesAsync();
    }

    public virtual async Task DisposeAsync()
    {
        await TruncateTablesAsync();

        if (_cluster is not null)
            await _cluster.ShutdownAsync();
    }

    private async Task CreateTablesAsync()
    {
        await Session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS messages_by_channel (
                channel_id bigint, message_id bigint, user_id bigint,
                content text, attachment_ids list<bigint>, mention_ids list<bigint>,
                reply_to_id bigint, is_deleted boolean, is_edited boolean,
                edited_at timestamp, message_type varchar,
                PRIMARY KEY (channel_id, message_id)
            ) WITH CLUSTERING ORDER BY (message_id DESC)"
            )
        );

        await Session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS messages_by_id (
                message_id bigint PRIMARY KEY, channel_id bigint, user_id bigint,
                content text, attachment_ids list<bigint>, reply_to_id bigint,
                is_deleted boolean, is_edited boolean, edited_at timestamp)"
            )
        );

        await Session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS read_states (
                user_id bigint, channel_id bigint, last_read_message_id bigint,
                PRIMARY KEY (user_id, channel_id))"
            )
        );

        await Session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS pinned_messages (
                channel_id bigint, pinned_at bigint, message_id bigint, pinned_by bigint,
                PRIMARY KEY (channel_id, pinned_at)
            ) WITH CLUSTERING ORDER BY (pinned_at DESC)"
            )
        );
    }

    protected async Task TruncateTablesAsync()
    {
        foreach (var table in TablesToTruncate)
            await Session.ExecuteAsync(new SimpleStatement($"TRUNCATE {table}"));
    }
}
