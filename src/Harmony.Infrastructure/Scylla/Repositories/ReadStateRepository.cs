using Cassandra;
using Harmony.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla.Repositories;

public class ReadStateRepository : IReadStateRepository
{
    private readonly ISession _session;
    private readonly ReadStateStatements _statements;
    private readonly ILogger<ReadStateRepository> _logger;

    public ReadStateRepository(
        IScyllaSessionFactory factory,
        ReadStateStatements statements,
        ILogger<ReadStateRepository> logger
    )
    {
        _session = factory.Session;
        _statements = statements;
        _logger = logger;
    }

    public async Task MarkAsReadAsync(
        long userId,
        long channelId,
        long lastReadMessageId,
        CancellationToken ct = default
    )
    {
        var bound = _statements.Upsert.Bind(userId, channelId, lastReadMessageId);
        bound.SetIdempotence(true); // Constant value assignment is safe to retry/speculate
        await _session.ExecuteAsync(bound, "read-states");

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
        var bound = _statements.SelectOne.Bind(userId, channelId);
        bound.SetIdempotence(true);
        var rows = await _session.ExecuteAsync(bound, "read-states");
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
            var bound = _statements.SelectOne.Bind(userId, channelId);
            bound.SetIdempotence(true);
            var rows = await _session.ExecuteAsync(bound, "read-states");
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
