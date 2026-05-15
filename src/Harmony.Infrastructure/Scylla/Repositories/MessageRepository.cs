using Cassandra;
using Cassandra.Data.Linq;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Scylla.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly ISession _session;
    private readonly ILogger<MessageRepository> _logger;

    // Prepared statements — compiled once on first use, reused on every call
    private readonly Lazy<PreparedStatement> _insertByChannel;
    private readonly Lazy<PreparedStatement> _insertById;
    private readonly Lazy<PreparedStatement> _selectByChannel;
    private readonly Lazy<PreparedStatement> _selectByChannelBefore;
    private readonly Lazy<PreparedStatement> _selectById;
    private readonly Lazy<PreparedStatement> _softDeleteByChannel;
    private readonly Lazy<PreparedStatement> _softDeleteById;
    private readonly Lazy<PreparedStatement> _editByChannel;
    private readonly Lazy<PreparedStatement> _editById;
    private readonly Lazy<PreparedStatement> _insertPinned;
    private readonly Lazy<PreparedStatement> _deletePinned;
    private readonly Lazy<PreparedStatement> _selectPinned;

    public MessageRepository(ScyllaSessionFactory factory, ILogger<MessageRepository> logger)
    {
        _session = factory.Session;
        _logger = logger;

        _insertByChannel = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            INSERT INTO messages_by_channel
                (channel_id, message_id, user_id, content, attachment_ids,
                 mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
            )
        );

        _insertById = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            INSERT INTO messages_by_id
                (message_id, channel_id, user_id, content,
                 attachment_ids, reply_to_id, is_deleted, is_edited, edited_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"
            )
        );

        _selectByChannel = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            SELECT channel_id, message_id, user_id, content, attachment_ids,
                   mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type
            FROM messages_by_channel
            WHERE channel_id = ?
            LIMIT ?"
            )
        );

        _selectByChannelBefore = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            SELECT channel_id, message_id, user_id, content, attachment_ids,
                   mention_ids, reply_to_id, is_deleted, is_edited, edited_at, message_type
            FROM messages_by_channel
            WHERE channel_id = ? AND message_id < ?
            LIMIT ?"
            )
        );

        _selectById = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            SELECT message_id, channel_id, user_id, content, attachment_ids,
                   reply_to_id, is_deleted, is_edited, edited_at
            FROM messages_by_id
            WHERE message_id = ?"
            )
        );

        _softDeleteByChannel = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            UPDATE messages_by_channel
            SET is_deleted = true
            WHERE channel_id = ? AND message_id = ?"
            )
        );

        _softDeleteById = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            UPDATE messages_by_id
            SET is_deleted = true
            WHERE message_id = ?"
            )
        );

        _editByChannel = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            UPDATE messages_by_channel
            SET content = ?, is_edited = true, edited_at = ?
            WHERE channel_id = ? AND message_id = ?"
            )
        );

        _editById = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            UPDATE messages_by_id
            SET content = ?, is_edited = true, edited_at = ?
            WHERE message_id = ?"
            )
        );

        _insertPinned = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            INSERT INTO pinned_messages (channel_id, pinned_at, message_id, pinned_by)
            VALUES (?, ?, ?, ?)"
            )
        );

        _deletePinned = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            DELETE FROM pinned_messages
            WHERE channel_id = ? AND pinned_at = ?"
            )
        );

        _selectPinned = new Lazy<PreparedStatement>(() =>
            _session.Prepare(
                @"
            SELECT channel_id, pinned_at, message_id, pinned_by
            FROM pinned_messages
            WHERE channel_id = ?"
            )
        );
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
            var bound = _selectByChannelBefore.Value.Bind(channelId, beforeMessageId.Value, limit);
            rows = await _session.ExecuteAsync(bound);
        }
        else
        {
            var bound = _selectByChannel.Value.Bind(channelId, limit);
            rows = await _session.ExecuteAsync(bound);
        }

        return rows.Select(MapMessage);
    }

    public async Task<Message?> GetByIdAsync(long messageId, CancellationToken ct = default)
    {
        var bound = _selectById.Value.Bind(messageId);
        var rows = await _session.ExecuteAsync(bound);
        var row = rows.FirstOrDefault();
        return row is null ? null : MapMessageById(row);
    }

    public async Task SaveAsync(Message message, CancellationToken ct = default)
    {
        // Dual-write to both tables — fire both and await together
        var byChannel = _session.ExecuteAsync(
            _insertByChannel.Value.Bind(
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
            _insertById.Value.Bind(
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
        var byChannel = _session.ExecuteAsync(
            _softDeleteByChannel.Value.Bind(channelId, messageId)
        );

        var byId = _session.ExecuteAsync(_softDeleteById.Value.Bind(messageId));

        await Task.WhenAll(byChannel, byId);
    }

    public async Task EditAsync(
        long messageId,
        long channelId,
        string newContent,
        CancellationToken ct = default
    )
    {
        var now = DateTime.UtcNow;

        var byChannel = _session.ExecuteAsync(
            _editByChannel.Value.Bind(newContent, now, channelId, messageId)
        );

        var byId = _session.ExecuteAsync(_editById.Value.Bind(newContent, now, messageId));

        await Task.WhenAll(byChannel, byId);
    }

    public async Task PinAsync(
        long channelId,
        long messageId,
        long pinnedBy,
        CancellationToken ct = default
    )
    {
        // pinnedAt is a Snowflake ID used as a clustering key — use messageId as pinned_at
        // so pins are ordered by when the message was sent, not when it was pinned
        var bound = _insertPinned.Value.Bind(channelId, messageId, messageId, pinnedBy);
        await _session.ExecuteAsync(bound);
    }

    public async Task UnpinAsync(long channelId, long pinnedAt, CancellationToken ct = default)
    {
        var bound = _deletePinned.Value.Bind(channelId, pinnedAt);
        await _session.ExecuteAsync(bound);
    }

    public async Task<IEnumerable<PinnedMessage>> GetPinnedAsync(
        long channelId,
        CancellationToken ct = default
    )
    {
        var bound = _selectPinned.Value.Bind(channelId);
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
