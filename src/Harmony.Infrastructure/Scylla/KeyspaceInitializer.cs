using Cassandra;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla;

public class KeyspaceInitializer : IHostedService
{
    private readonly ISession _session;
    private readonly ILogger<KeyspaceInitializer> _logger;

    public KeyspaceInitializer(ScyllaSessionFactory factory, ILogger<KeyspaceInitializer> logger)
    {
        _session = factory.Session;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing ScyllaDB schema...");

        await CreateMessagesTableAsync();
        await CreateReadStatesTableAsync();
        await CreateMessagesByIdTableAsync();
        await CreatePinnedMessagesTableAsync();

        _logger.LogInformation("ScyllaDB schema initialization complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CreateMessagesTableAsync()
    {
        await _session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS messages_by_channel (
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
                PRIMARY KEY (channel_id, message_id)
            ) WITH CLUSTERING ORDER BY (message_id DESC)
              AND compaction = {
                'class': 'TimeWindowCompactionStrategy',
                'compaction_window_unit': 'DAYS',
                'compaction_window_size': 1
              }"
            )
        );

        _logger.LogDebug("Table messages_by_channel ready");
    }

    private async Task CreateReadStatesTableAsync()
    {
        await _session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS read_states (
                user_id bigint,
                channel_id bigint,
                last_read_message_id bigint,
                PRIMARY KEY (user_id, channel_id)
            )"
            )
        );

        _logger.LogDebug("Table read_states ready");
    }

    private async Task CreateMessagesByIdTableAsync()
    {
        await _session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS messages_by_id (
                message_id bigint PRIMARY KEY,
                channel_id bigint,
                user_id bigint,
                content text,
                attachment_ids list<bigint>,
                reply_to_id bigint,
                is_deleted boolean,
                is_edited boolean,
                edited_at timestamp
            )"
            )
        );

        _logger.LogDebug("Table messages_by_id ready");
    }

    private async Task CreatePinnedMessagesTableAsync()
    {
        await _session.ExecuteAsync(
            new SimpleStatement(
                @"
            CREATE TABLE IF NOT EXISTS pinned_messages (
                channel_id bigint,
                pinned_at bigint,
                message_id bigint,
                pinned_by bigint,
                PRIMARY KEY (channel_id, pinned_at)
            ) WITH CLUSTERING ORDER BY (pinned_at DESC)"
            )
        );

        _logger.LogDebug("Table pinned_messages ready");
    }
}
