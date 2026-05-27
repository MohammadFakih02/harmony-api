using Cassandra;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Infrastructure.Scylla;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly ISession _session;
    private readonly ILogger<MessageRepository> _logger;
    private readonly string _ks;

    private PreparedStatement? _insertByChannel;
    private PreparedStatement? _insertById;
    private PreparedStatement? _selectByChannel;
    private PreparedStatement? _selectByChannelBefore;
    private PreparedStatement? _selectById;
    private PreparedStatement? _softDeleteByChannel;
    private PreparedStatement? _softDeleteById;
    private PreparedStatement? _editByChannel;
    private PreparedStatement? _editById;
    private PreparedStatement? _insertPinned;
    private PreparedStatement? _deletePinned;
    private PreparedStatement? _selectPinned;

    public MessageRepository(IScyllaSessionFactory factory, ILogger<MessageRepository> logger)
    {
        _session = factory.Session;
        _ks = factory.Keyspace;
        _logger = logger;
    }

    // --- Prepared statement accessors ---
    // Prepared inside method calls so Polly circuit breaker wraps both preparation and execution

    private async Task<PreparedStatement> GetInsertByChannelAsync() =>
        _insertByChannel ??= await _session.PrepareAsync(
            $@"
            INSERT INTO {_ks}.messages_by_channel
                (channel_id, message_id, user_id, content, attachment_ids,
                 mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
        );

    private async Task<PreparedStatement> GetInsertByIdAsync() =>
        _insertById ??= await _session.PrepareAsync(
            $@"
            INSERT INTO {_ks}.messages_by_id
                (message_id, channel_id, user_id, content,
                 attachment_ids, reply_to_id, is_deleted, is_edited, edited_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"
        );

    private async Task<PreparedStatement> GetSelectByChannelAsync() =>
        _selectByChannel ??= await _session.PrepareAsync(
            $@"
            SELECT channel_id, message_id, user_id, content, attachment_ids,
                   mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type
            FROM {_ks}.messages_by_channel
            WHERE channel_id = ?
            LIMIT ?"
        );

    private async Task<PreparedStatement> GetSelectByChannelBeforeAsync() =>
        _selectByChannelBefore ??= await _session.PrepareAsync(
            $@"
            SELECT channel_id, message_id, user_id, content, attachment_ids,
                   mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type
            FROM {_ks}.messages_by_channel
            WHERE channel_id = ? AND message_id < ?
            LIMIT ?"
        );

    private async Task<PreparedStatement> GetSelectByIdAsync() =>
        _selectById ??= await _session.PrepareAsync(
            $@"
            SELECT message_id, channel_id, user_id, content, attachment_ids,
                   reply_to_id, is_deleted, is_edited, edited_at
            FROM {_ks}.messages_by_id
            WHERE message_id = ?"
        );

    private async Task<PreparedStatement> GetSoftDeleteByChannelAsync() =>
        _softDeleteByChannel ??= await _session.PrepareAsync(
            $@"
            UPDATE {_ks}.messages_by_channel
            SET is_deleted = true
            WHERE channel_id = ? AND message_id = ?"
        );

    private async Task<PreparedStatement> GetSoftDeleteByIdAsync() =>
        _softDeleteById ??= await _session.PrepareAsync(
            $@"
            UPDATE {_ks}.messages_by_id
            SET is_deleted = true
            WHERE message_id = ?"
        );

    private async Task<PreparedStatement> GetEditByChannelAsync() =>
        _editByChannel ??= await _session.PrepareAsync(
            $@"
            UPDATE {_ks}.messages_by_channel
            SET content = ?, is_edited = true, edited_at = ?
            WHERE channel_id = ? AND message_id = ?"
        );

    private async Task<PreparedStatement> GetEditByIdAsync() =>
        _editById ??= await _session.PrepareAsync(
            $@"
            UPDATE {_ks}.messages_by_id
            SET content = ?, is_edited = true, edited_at = ?
            WHERE message_id = ?"
        );

    private async Task<PreparedStatement> GetInsertPinnedAsync() =>
        _insertPinned ??= await _session.PrepareAsync(
            $@"
            INSERT INTO {_ks}.pinned_messages
                (channel_id, pinned_at, message_id, pinned_by)
            VALUES (?, ?, ?, ?)"
        );

    private async Task<PreparedStatement> GetDeletePinnedAsync() =>
        _deletePinned ??= await _session.PrepareAsync(
            $@"
            DELETE FROM {_ks}.pinned_messages
            WHERE channel_id = ? AND pinned_at = ?"
        );

    private async Task<PreparedStatement> GetSelectPinnedAsync() =>
        _selectPinned ??= await _session.PrepareAsync(
            $@"
            SELECT channel_id, pinned_at, message_id, pinned_by
            FROM {_ks}.pinned_messages
            WHERE channel_id = ?"
        );

    // --- Repository methods ---

    public async Task<IEnumerable<Message>> GetChannelMessagesAsync(
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    )
    {
        BoundStatement bound;

        if (beforeMessageId.HasValue)
        {
            var stmt = await GetSelectByChannelBeforeAsync();
            bound = stmt.Bind(channelId, beforeMessageId.Value, limit);
        }
        else
        {
            var stmt = await GetSelectByChannelAsync();
            bound = stmt.Bind(channelId, limit);
        }

        // Mark as idempotent — enables speculative execution and safe retries
        bound.SetIdempotence(true);

        var rows = await _session.ExecuteAsync(bound);
        return rows.Select(MapMessage);
    }

    public async Task<Message?> GetByIdAsync(long messageId, CancellationToken ct = default)
    {
        var stmt = await GetSelectByIdAsync();
        var bound = stmt.Bind(messageId);

        // Mark as idempotent — enables speculative execution and safe retries
        bound.SetIdempotence(true);

        var rows = await _session.ExecuteAsync(bound);
        var row = rows.FirstOrDefault();
        return row is null ? null : MapMessageById(row);
    }

    public async Task SaveAsync(Message message, CancellationToken ct = default)
    {
        var byChannelStmt = await GetInsertByChannelAsync();
        var byIdStmt = await GetInsertByIdAsync();

        // Writes are NOT idempotent — never marked SetIdempotence(true)
        // IdempotenceAwareRetryPolicy will not retry these on timeout
        var byChannel = _session.ExecuteAsync(
            byChannelStmt.Bind(
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
            )
        );

        var byId = _session.ExecuteAsync(
            byIdStmt.Bind(
                message.MessageId,
                message.ChannelId,
                message.UserId,
                message.Content,
                message.AttachmentIds,
                message.ReplyToId,
                false,
                false,
                null
            )
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
        var byChannelStmt = await GetSoftDeleteByChannelAsync();
        var byIdStmt = await GetSoftDeleteByIdAsync();

        // Soft deletes are idempotent — setting is_deleted=true twice is safe
        var byChannel = byChannelStmt.Bind(channelId, messageId);
        byChannel.SetIdempotence(true);

        var byId = byIdStmt.Bind(messageId);
        byId.SetIdempotence(true);

        await Task.WhenAll(_session.ExecuteAsync(byChannel), _session.ExecuteAsync(byId));
    }

    public async Task EditAsync(
        long messageId,
        long channelId,
        string newContent,
        CancellationToken ct = default
    )
    {
        var now = DateTime.UtcNow;
        var byChannelStmt = await GetEditByChannelAsync();
        var byIdStmt = await GetEditByIdAsync();

        // Edits are NOT idempotent — retrying with same timestamp could overwrite a newer edit
        await Task.WhenAll(
            _session.ExecuteAsync(byChannelStmt.Bind(newContent, now, channelId, messageId)),
            _session.ExecuteAsync(byIdStmt.Bind(newContent, now, messageId))
        );
    }

    public async Task PinAsync(
        long channelId,
        long messageId,
        long pinnedBy,
        CancellationToken ct = default
    )
    {
        var stmt = await GetInsertPinnedAsync();
        // INSERT is not idempotent by default
        await _session.ExecuteAsync(stmt.Bind(channelId, messageId, messageId, pinnedBy));
    }

    public async Task UnpinAsync(long channelId, long pinnedAt, CancellationToken ct = default)
    {
        var stmt = await GetDeletePinnedAsync();
        var bound = stmt.Bind(channelId, pinnedAt);
        // DELETE is idempotent — deleting an already-deleted row is safe
        bound.SetIdempotence(true);
        await _session.ExecuteAsync(bound);
    }

    public async Task<IEnumerable<PinnedMessage>> GetPinnedAsync(
        long channelId,
        CancellationToken ct = default
    )
    {
        var stmt = await GetSelectPinnedAsync();
        var bound = stmt.Bind(channelId);
        bound.SetIdempotence(true);
        var rows = await _session.ExecuteAsync(bound);
        return rows.Select(MapPinnedMessage);
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
