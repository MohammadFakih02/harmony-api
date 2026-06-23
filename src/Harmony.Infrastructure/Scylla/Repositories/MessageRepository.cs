using Cassandra;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly ISession _session;
    private readonly MessageStatements _statements;
    private readonly ILogger<MessageRepository> _logger;

    public MessageRepository(
        IScyllaSessionFactory factory,
        MessageStatements statements,
        ILogger<MessageRepository> logger
    )
    {
        _session = factory.Session;
        _statements = statements;
        _logger = logger;
    }

    public async Task<IEnumerable<Message>> GetChannelMessagesAsync(
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    )
    {
        RowSet rows;

        if (beforeMessageId.HasValue)
        {
            var bound = _statements.SelectByChannelBefore.Bind(
                channelId,
                beforeMessageId.Value,
                limit
            );
            bound.SetIdempotence(true);
            rows = await _session.ExecuteAsync(bound, "read");
        }
        else
        {
            var bound = _statements.SelectByChannel.Bind(channelId, limit);
            bound.SetIdempotence(true);
            rows = await _session.ExecuteAsync(bound, "read");
        }

        return rows.Select(MapMessage).ToList();
    }

    public async Task<Message?> GetByIdAsync(long messageId, CancellationToken ct = default)
    {
        var bound = _statements.SelectById.Bind(messageId);
        bound.SetIdempotence(true);
        var rows = await _session.ExecuteAsync(bound, "read");
        var row = rows.FirstOrDefault();
        return row is null ? null : MapMessageById(row);
    }

    public async Task SaveAsync(Message message, CancellationToken ct = default)
    {
        var byChannel = _session.ExecuteAsync(
            _statements.InsertByChannel.Bind(
                message.ChannelId,
                message.MessageId,
                message.UserId,
                message.Content,
                message.AttachmentIds,
                message.MentionIds,
                message.ReplyToId,
                false,
                false,
                null,
                message.MessageType
            ),
            "write"
        );

        var byId = _session.ExecuteAsync(
            _statements.InsertById.Bind(
                message.MessageId,
                message.ChannelId,
                message.UserId,
                message.Content,
                message.AttachmentIds,
                message.MentionIds,
                message.ReplyToId,
                false,
                false,
                null
            ),
            "write"
        );

        await Task.WhenAll(byChannel, byId);

        _logger.LogDebug(
            "Saved message {MessageId} to channel {ChannelId}",
            message.MessageId,
            message.ChannelId
        );
    }

    public async Task DeleteAsync(long messageId, long channelId, CancellationToken ct = default)
    {
        var byChannel = _session.ExecuteAsync(
            _statements.SoftDeleteByChannel.Bind(channelId, messageId),
            "write"
        );
        var byId = _session.ExecuteAsync(_statements.SoftDeleteById.Bind(messageId), "write");
        await Task.WhenAll(byChannel, byId);
    }

    public async Task EditAsync(
        long messageId,
        long channelId,
        string newContent,
        List<long> mentionIds,
        CancellationToken ct = default
    )
    {
        var now = DateTime.UtcNow;
        var byChannel = _session.ExecuteAsync(
            _statements.EditByChannel.Bind(newContent, mentionIds, now, channelId, messageId),
            "write"
        );
        var byId = _session.ExecuteAsync(
            _statements.EditById.Bind(newContent, mentionIds, now, messageId),
            "write"
        );
        await Task.WhenAll(byChannel, byId);
    }

    public async Task PinAsync(
        long channelId,
        long messageId,
        long pinnedBy,
        CancellationToken ct = default
    )
    {
        var bound = _statements.InsertPinned.Bind(channelId, messageId, messageId, pinnedBy);
        await _session.ExecuteAsync(bound, "write");
    }

    public async Task UnpinAsync(long channelId, long pinnedAt, CancellationToken ct = default)
    {
        var bound = _statements.DeletePinned.Bind(channelId, pinnedAt);
        await _session.ExecuteAsync(bound, "write");
    }

    public async Task<IEnumerable<PinnedMessage>> GetPinnedAsync(
        long channelId,
        CancellationToken ct = default
    )
    {
        var bound = _statements.SelectPinned.Bind(channelId);
        bound.SetIdempotence(true);
        var rows = await _session.ExecuteAsync(bound, "read");
        return rows.Select(MapPinnedMessage).ToList();
    }

    public async Task PurgeChannelPartitionsAsync(long channelId, CancellationToken ct = default)
    {
        // Fire both partition deletes concurrently as O(1) writes [14]
        var deleteMessages = _session.ExecuteAsync(
            _statements.PurgeChannelMessages.Bind(channelId),
            "write"
        );

        var deletePins = _session.ExecuteAsync(
            _statements.PurgeChannelPins.Bind(channelId),
            "write"
        );

        await Task.WhenAll(deleteMessages, deletePins);

        _logger.LogInformation(
            "ScyllaDB: purged message and pin partitions for ChannelId: {ChannelId}",
            channelId
        );
    }

    // --- Mappers ---

    private static Message MapMessage(Row row) =>
        new()
        {
            ChannelId = row.GetValue<long>("channel_id"),
            MessageId = row.GetValue<long>("message_id"),
            UserId = row.GetValue<long>("user_id"),
            Content = row.GetValue<string>("content") ?? string.Empty,
            AttachmentIds = row.GetValue<List<long>>("attachment_ids") ?? [],
            MentionIds = row.GetValue<List<long>>("mention_ids") ?? [],
            ReplyToId = row.GetValue<long?>("reply_to_id"),
            IsDeleted = row.GetValue<bool>("is_deleted"),
            IsEdited = row.GetValue<bool>("is_edited"),
            EditedAt = row.GetValue<DateTime?>("edited_at"),
            MessageType = row.GetValue<string>("message_type") ?? "text",
        };

    private static Message MapMessageById(Row row) =>
        new()
        {
            MessageId = row.GetValue<long>("message_id"),
            ChannelId = row.GetValue<long>("channel_id"),
            UserId = row.GetValue<long>("user_id"),
            Content = row.GetValue<string>("content") ?? string.Empty,
            AttachmentIds = row.GetValue<List<long>>("attachment_ids") ?? [],
            MentionIds = row.GetValue<List<long>>("mention_ids") ?? [],
            ReplyToId = row.GetValue<long?>("reply_to_id"),
            IsDeleted = row.GetValue<bool>("is_deleted"),
            IsEdited = row.GetValue<bool>("is_edited"),
            EditedAt = row.GetValue<DateTime?>("edited_at"),
        };

    private static PinnedMessage MapPinnedMessage(Row row) =>
        new()
        {
            ChannelId = row.GetValue<long>("channel_id"),
            PinnedAt = row.GetValue<long>("pinned_at"),
            MessageId = row.GetValue<long>("message_id"),
            PinnedBy = row.GetValue<long>("pinned_by"),
        };
}
