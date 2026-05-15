using Harmony.Core.Domain.Entities;

namespace Harmony.Core.Interfaces.Repositories;

public interface IMessageRepository
{
    // messages_by_channel — paginated, newest first
    Task<IEnumerable<Message>> GetChannelMessagesAsync(
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    );

    // messages_by_id — single message lookup (for edits, deletes, replies)
    Task<Message?> GetByIdAsync(long messageId, CancellationToken ct = default);

    // Inserts into both messages_by_channel and messages_by_id (dual-write)
    Task SaveAsync(Message message, CancellationToken ct = default);

    // Soft delete — sets is_deleted = true in both tables
    Task DeleteAsync(long messageId, long channelId, CancellationToken ct = default);

    // Edit — updates content, is_edited = true, edited_at = now in both tables
    Task EditAsync(
        long messageId,
        long channelId,
        string newContent,
        CancellationToken ct = default
    );

    // pinned_messages table
    Task PinAsync(long channelId, long messageId, long pinnedBy, CancellationToken ct = default);
    Task UnpinAsync(long channelId, long pinnedAt, CancellationToken ct = default);
    Task<IEnumerable<PinnedMessage>> GetPinnedAsync(long channelId, CancellationToken ct = default);
}
