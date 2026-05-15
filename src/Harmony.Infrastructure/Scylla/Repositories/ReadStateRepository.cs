using Cassandra;
using Harmony.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla.Repositories;

public class ReadStateRepository : IReadStateRepository
{
    private readonly ISession _session;
    private readonly ILogger<ReadStateRepository> _logger;

    private readonly Lazy<PreparedStatement> _upsert;
    private readonly Lazy<PreparedStatement> _selectOne;
    private readonly Lazy<PreparedStatement> _selectForChannel;

    public ReadStateRepository(ScyllaSessionFactory factory, ILogger<ReadStateRepository> logger)
    {
        _session = factory.Session;
        _logger = logger;

        _upsert = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            INSERT INTO read_states (user_id, channel_id, last_read_message_id)
            VALUES (?, ?, ?)"
            )
        );

        _selectOne = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            SELECT last_read_message_id
            FROM read_states
            WHERE user_id = ? AND channel_id = ?"
            )
        );

        _selectForChannel = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            SELECT last_read_message_id
            FROM read_states
            WHERE user_id = ? AND channel_id = ?"
            )
        );
    }

    public async Task MarkAsReadAsync(
        long userId,
        long channelId,
        long lastReadMessageId,
        CancellationToken ct = default
    )
    {
        var bound = _upsert.Value.Bind(userId, channelId, lastReadMessageId);
        await _session.ExecuteAsync(bound);

        _logger.LogDebug(
            "Marked channel {ChannelId} as read for user {UserId} up to message {MessageId}",
            channelId,
            userId,
            lastReadMessageId
        );
    }

    public async Task<long?> GetLastReadMessageIdAsync(
        long userId,
        long channelId,
        CancellationToken ct = default
    )
    {
        var bound = _selectOne.Value.Bind(userId, channelId);
        var rows = await _session.ExecuteAsync(bound);
        var row = rows.FirstOrDefault();
        return row?.GetValue<long?>("last_read_message_id");
    }

    public async Task<IReadOnlyDictionary<long, long>> GetAllForUserAsync(
        long userId,
        IEnumerable<long> channelIds,
        CancellationToken ct = default
    )
    {
        // Fire all queries in parallel — one per channel
        // ScyllaDB's read_states PRIMARY KEY is (user_id, channel_id) so each
        // query hits exactly one partition — no scatter-gather, no secondary index needed
        var tasks = channelIds.Select(async channelId =>
        {
            var bound = _selectForChannel.Value.Bind(userId, channelId);
            var rows = await _session.ExecuteAsync(bound);
            var row = rows.FirstOrDefault();
            var lastRead = row?.GetValue<long?>("last_read_message_id");
            return (channelId, lastRead);
        });

        var results = await Task.WhenAll(tasks);

        return results
            .Where(r => r.lastRead.HasValue)
            .ToDictionary(r => r.channelId, r => r.lastRead!.Value);
    }
}
