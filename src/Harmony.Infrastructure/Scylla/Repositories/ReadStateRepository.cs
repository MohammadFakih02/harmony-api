using Cassandra;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Infrastructure.Scylla;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla.Repositories;

public class ReadStateRepository : IReadStateRepository
{
    private readonly ISession _session;
    private readonly ILogger<ReadStateRepository> _logger;
    private readonly string _ks;

    private PreparedStatement? _upsert;
    private PreparedStatement? _selectOne;

    public ReadStateRepository(IScyllaSessionFactory factory, ILogger<ReadStateRepository> logger)
    {
        _session = factory.Session;
        _ks = factory.Keyspace;
        _logger = logger;
    }

    private async Task<PreparedStatement> GetUpsertAsync() =>
        _upsert ??= await _session.PrepareAsync(
            $@"
            INSERT INTO {_ks}.read_states (user_id, channel_id, last_read_message_id)
            VALUES (?, ?, ?)"
        );

    private async Task<PreparedStatement> GetSelectOneAsync() =>
        _selectOne ??= await _session.PrepareAsync(
            $@"
            SELECT last_read_message_id
            FROM {_ks}.read_states
            WHERE user_id = ? AND channel_id = ?"
        );

    public async Task MarkAsReadAsync(
        long userId,
        long channelId,
        long lastReadMessageId,
        CancellationToken ct = default
    )
    {
        var stmt = await GetUpsertAsync();
        await _session.ExecuteAsync(stmt.Bind(userId, channelId, lastReadMessageId));

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
        var stmt = await GetSelectOneAsync();
        var rows = await _session.ExecuteAsync(stmt.Bind(userId, channelId));
        var row = rows.FirstOrDefault();
        return row?.GetValue<long?>("last_read_message_id");
    }

    public async Task<IReadOnlyDictionary<long, long>> GetAllForUserAsync(
        long userId,
        IEnumerable<long> channelIds,
        CancellationToken ct = default
    )
    {
        var tasks = channelIds.Select(async channelId =>
        {
            var stmt = await GetSelectOneAsync();
            var rows = await _session.ExecuteAsync(stmt.Bind(userId, channelId));
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
