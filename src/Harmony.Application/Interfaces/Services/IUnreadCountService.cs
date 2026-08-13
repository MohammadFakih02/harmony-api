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
    /// Called after a message is persisted. Branches on guild:
    /// <list type="bullet">
    /// <item>Guild channel (D3): O(1) — increments the per-channel message counter and sends ONE
    /// guild-group <c>ChannelActivity</c> ping; unread is computed read-time as counter − mark, so
    /// there is no per-recipient work.</item>
    /// <item>DM (guildId null): the bounded per-participant path — one INCR of
    /// <c>unread:{userId}:{channelId}</c> each plus an absolute <c>UnreadCountUpdated</c>.</item>
    /// </list>
    /// Best-effort: never throws into the caller (the consumer's ack).
    /// </summary>
    Task IncrementForChannelAsync(
        long? guildId,
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
        long? guildId,
        long channelId,
        long lastReadMessageId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Reads current unread counts for the given guild channels (sidebar / bootstrap load), keyed
    /// channelId → guildId. Computed read-time as channel-counter − the user's mark, after a
    /// ViewChannel filter drops override-hidden channels. A channel with no mark is treated as
    /// caught-up (0) and lazily anchored to the current count. Returns only channels with count
    /// &gt; 0; Redis down / empty input =&gt; empty.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> GetUnreadForUserAsync(
        long userId,
        IReadOnlyDictionary<long, long> channelGuildMap,
        CancellationToken ct = default
    );
}
