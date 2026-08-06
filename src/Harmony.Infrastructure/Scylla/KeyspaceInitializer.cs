using Cassandra;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla;

public class KeyspaceInitializer : IHostedService
{
    private readonly IScyllaSessionFactory _factory;
    private readonly ILogger<KeyspaceInitializer> _logger;

    public KeyspaceInitializer(IScyllaSessionFactory factory, ILogger<KeyspaceInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing ScyllaDB schema...");

        // Session is evaluated lazily here inside the asynchronous lifecycle method
        var session = _factory.Session;
        var keyspace = _factory.Keyspace;

        await CreateMessagesTableAsync(session, keyspace);
        await CreateMessagesByIdTableAsync(session, keyspace);
        await CreateReadStatesTableAsync(session, keyspace);
        await CreatePinnedMessagesTableAsync(session, keyspace);

        _logger.LogInformation("ScyllaDB schema initialization complete.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CreateMessagesTableAsync(ISession session, string keyspace)
    {
        await session.ExecuteAsync(
            new SimpleStatement(
                $@"CREATE TABLE IF NOT EXISTS {keyspace}.messages_by_channel (
                    channel_id bigint,
                    message_id bigint,
                    user_id bigint,
                    content text,
                    attachment_ids list<bigint>,
                    mention_ids list<bigint>,
                    reply_to_id bigint,
                    is_deleted boolean,
                    is_edited boolean,
                    edited_at timestamp,
                    message_type varchar,
                    forward_snapshot text,
                    PRIMARY KEY (channel_id, message_id)
                ) WITH CLUSTERING ORDER BY (message_id DESC)
                  AND compaction = {{
                    'class': 'TimeWindowCompactionStrategy',
                    'compaction_window_unit': 'DAYS',
                    'compaction_window_size': 1
                  }}"
            )
        );
        _logger.LogDebug("Table messages_by_channel ready.");
    }

    private async Task CreateMessagesByIdTableAsync(ISession session, string keyspace)
    {
        await session.ExecuteAsync(
            new SimpleStatement(
                $@"CREATE TABLE IF NOT EXISTS {keyspace}.messages_by_id (
                    message_id bigint PRIMARY KEY,
                    channel_id bigint,
                    user_id bigint,
                    content text,
                    attachment_ids list<bigint>,
                    mention_ids list<bigint>,
                    reply_to_id bigint,
                    is_deleted boolean,
                    is_edited boolean,
                    edited_at timestamp
                )"
            )
        );
        _logger.LogDebug("Table messages_by_id ready.");
    }

    private async Task CreateReadStatesTableAsync(ISession session, string keyspace)
    {
        await session.ExecuteAsync(
            new SimpleStatement(
                $@"CREATE TABLE IF NOT EXISTS {keyspace}.read_states (
                    user_id bigint,
                    channel_id bigint,
                    last_read_message_id bigint,
                    PRIMARY KEY (user_id, channel_id)
                )"
            )
        );
        _logger.LogDebug("Table read_states ready.");
    }

    private async Task CreatePinnedMessagesTableAsync(ISession session, string keyspace)
    {
        await session.ExecuteAsync(
            new SimpleStatement(
                $@"CREATE TABLE IF NOT EXISTS {keyspace}.pinned_messages (
                    channel_id bigint,
                    pinned_at bigint,
                    message_id bigint,
                    pinned_by bigint,
                    PRIMARY KEY (channel_id, pinned_at)
                ) WITH CLUSTERING ORDER BY (pinned_at DESC)"
            )
        );
        _logger.LogDebug("Table pinned_messages ready.");
    }
}
