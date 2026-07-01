using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IMessageRepository
{
    // messages_by_channel — paginated, newest first
    Task<IEnumerable<Message>> GetChannelMessagesAsync(
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    );

    // messages_by_channel — a window centred on a message (for jump-to / search results),
    // newest first. Returns ~limit messages straddling messageId (half newer-or-equal, half older).
    Task<IEnumerable<Message>> GetMessagesAroundAsync(
        long channelId,
        long messageId,
        int limit = 50,
        CancellationToken ct = default
    );

    // messages_by_channel — a strictly-newer page (scroll-down after a jump), newest first.
    Task<IEnumerable<Message>> GetMessagesAfterAsync(
        long channelId,
        long afterMessageId,
        int limit = 50,
        CancellationToken ct = default
    );

    // messages_by_id — single message lookup (for edits, deletes, replies)
    Task<Message?> GetByIdAsync(long messageId, CancellationToken ct = default);

    // Inserts into both messages_by_channel and messages_by_id (dual-write)
    Task SaveAsync(Message message, CancellationToken ct = default);

    // Soft delete — sets is_deleted = true in both tables
    Task DeleteAsync(long messageId, long channelId, CancellationToken ct = default);

    // Edit — updates content, mention_ids, is_edited = true, edited_at = now in both tables
    Task EditAsync(
        long messageId,
        long channelId,
        string newContent,
        List<long> mentionIds,
        CancellationToken ct = default
    );

    // pinned_messages table
    Task PinAsync(long channelId, long messageId, long pinnedBy, CancellationToken ct = default);
    Task UnpinAsync(long channelId, long pinnedAt, CancellationToken ct = default);
    Task<IEnumerable<PinnedMessage>> GetPinnedAsync(long channelId, CancellationToken ct = default);

    /// <summary>Executes a high-performance partition purge in ScyllaDB [14].</summary>
    Task PurgeChannelPartitionsAsync(long channelId, CancellationToken ct = default);
}
