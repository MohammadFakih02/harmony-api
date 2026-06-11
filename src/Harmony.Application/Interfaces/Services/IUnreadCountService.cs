namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Manages per-user unread counts. Redis holds the counts as a cache
/// (unread:{userId}:{channelId}); ScyllaDB read_states is the source of truth.
///
/// All Redis interaction fails open: if Redis is unavailable the counts are
/// simply not updated/read, never throwing — a missing badge is far preferable
/// to a failed message pipeline. Per-member broadcast failures are swallowed
/// individually so one dead connection never aborts the fan-out.
/// </summary>
public interface IUnreadCountService
{
    /// <summary>
    /// After a message is persisted, increments the unread count for every
    /// channel recipient except the sender, then pushes UnreadCountUpdated to each.
    /// Recipient resolution today = guild members; DMs branch here later.
    /// Best-effort: never throws into the caller (the consumer's ack).
    /// </summary>
    Task IncrementForChannelAsync(
        long guildId,
        long channelId,
        long senderUserId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Marks a channel read for a user: writes read_states (truth) first, then
    /// clears the Redis cache key, then pushes a zero count for multi-device sync.
    /// The read_states write is NOT swallowed — if truth can't be written, the
    /// caller must hear about it. Cache clear and broadcast are best-effort.
    /// </summary>
    Task MarkReadAsync(
        long userId,
        long guildId,
        long channelId,
        long lastReadMessageId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Reads current unread counts for the given channels (sidebar load).
    /// Returns only channels with a count &gt; 0. Absent keys / Redis down =&gt; empty.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> GetUnreadForUserAsync(
        long userId,
        IEnumerable<long> channelIds,
        CancellationToken ct = default
    );
}
