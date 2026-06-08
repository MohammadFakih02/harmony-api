using Cassandra;

namespace Harmony.IntegrationTests.Infrastructure;

public abstract class ScyllaTestBase : IAsyncLifetime
{
    private static readonly object Lock = new();
    private static ICluster? _sharedCluster;
    private static ISession? _sharedSession;

    protected ISession Session { get; private set; } = null!;

    protected abstract IEnumerable<string> TablesToTruncate { get; }

    public virtual async Task InitializeAsync()
    {
        // Thread-safe initialize the shared connection pool once for the entire test run
        if (_sharedSession is null)
        {
            lock (Lock)
            {
                if (_sharedSession is null)
                {
                    _sharedCluster = Cluster
                        .Builder()
                        .AddContactPoint("127.0.0.1")
                        .WithPort(9042)
                        .WithAddressTranslator(
                            new Harmony.Infrastructure.Scylla.LocalhostAddressTranslator()
                        ) // Force localhost routing
                        .WithLoadBalancingPolicy(Policies.DefaultLoadBalancingPolicy)
                        .WithReconnectionPolicy(new ExponentialReconnectionPolicy(1000, 60000))
                        .WithQueryOptions(
                            new QueryOptions().SetConsistencyLevel(ConsistencyLevel.LocalQuorum)
                        )
                        .WithExecutionProfiles(options =>
                            options
                                .WithProfile(
                                    "default",
                                    profile =>
                                        profile
                                            .WithLoadBalancingPolicy(
                                                Policies.DefaultLoadBalancingPolicy
                                            )
                                            .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
                                            .WithRetryPolicy(
                                                new LoggingRetryPolicy(new DefaultRetryPolicy())
                                            )
                                )
                                .WithProfile(
                                    "write",
                                    profile =>
                                        profile
                                            .WithLoadBalancingPolicy(
                                                Policies.DefaultLoadBalancingPolicy
                                            )
                                            .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
                                            .WithRetryPolicy(
                                                new LoggingRetryPolicy(new DefaultRetryPolicy())
                                            )
                                )
                                .WithProfile(
                                    "read",
                                    profile =>
                                        profile
                                            .WithLoadBalancingPolicy(
                                                Policies.DefaultLoadBalancingPolicy
                                            )
                                            .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
                                            .WithRetryPolicy(
                                                new LoggingRetryPolicy(new DefaultRetryPolicy())
                                            )
                                            .WithSpeculativeExecutionPolicy(
                                                new ConstantSpeculativeExecutionPolicy(200, 1)
                                            )
                                )
                                .WithProfile(
                                    "read-states",
                                    profile =>
                                        profile
                                            .WithLoadBalancingPolicy(
                                                Policies.DefaultLoadBalancingPolicy
                                            )
                                            .WithConsistencyLevel(ConsistencyLevel.LocalOne)
                                            .WithRetryPolicy(
                                                new LoggingRetryPolicy(new DefaultRetryPolicy())
                                            )
                                            .WithSpeculativeExecutionPolicy(
                                                new ConstantSpeculativeExecutionPolicy(100, 1)
                                            )
                                )
                        )
                        .Build();

                    using var bootstrapSession = _sharedCluster.Connect();
                    bootstrapSession.Execute(
                        "CREATE KEYSPACE IF NOT EXISTS harmony_test "
                            + "WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}"
                    );

                    _sharedSession = _sharedCluster.Connect("harmony_test");

                    // Compile all required database tables once
                    CreateTables(_sharedSession);
                }
            }
        }

        Session = _sharedSession;

        // Cleanly wipe data rows between test executions
        await TruncateTablesAsync();
    }

    public virtual Task DisposeAsync()
    {
        // Shared session is kept alive throughout the test execution run
        return Task.CompletedTask;
    }

    private static void CreateTables(ISession session)
    {
        session.Execute(
            @"CREATE TABLE IF NOT EXISTS messages_by_channel (
                channel_id bigint, message_id bigint, user_id bigint,
                content text, attachment_ids list<bigint>, mention_ids list<bigint>,
                reply_to_id bigint, is_deleted boolean, is_edited boolean,
                edited_at timestamp, message_type varchar,
                PRIMARY KEY (channel_id, message_id)
            ) WITH CLUSTERING ORDER BY (message_id DESC)"
        );

        session.Execute(
            @"CREATE TABLE IF NOT EXISTS messages_by_id (
                message_id bigint PRIMARY KEY, channel_id bigint, user_id bigint,
                content text, attachment_ids list<bigint>, reply_to_id bigint,
                is_deleted boolean, is_edited boolean, edited_at timestamp)"
        );

        session.Execute(
            @"CREATE TABLE IF NOT EXISTS read_states (
                user_id bigint, channel_id bigint, last_read_message_id bigint,
                PRIMARY KEY (user_id, channel_id))"
        );

        session.Execute(
            @"CREATE TABLE IF NOT EXISTS pinned_messages (
                channel_id bigint, pinned_at bigint, message_id bigint, pinned_by bigint,
                PRIMARY KEY (channel_id, pinned_at)
                ) WITH CLUSTERING ORDER BY (pinned_at DESC)"
        );
    }

    protected async Task TruncateTablesAsync()
    {
        foreach (var table in TablesToTruncate)
        {
            await Session.ExecuteAsync(new SimpleStatement($"TRUNCATE {table}"));
        }
    }
}
