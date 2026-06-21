using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Hubs;

/// <summary>
/// Strongly-typed contract for all server → client SignalR push events.
///
/// Lives in Harmony.Application so both Harmony.API (ChatHub) and
/// Harmony.Infrastructure (ScyllaMessageConsumer broadcast) can reference it
/// without creating a circular dependency.
/// </summary>
public interface IChatClient
{
    /// <summary>
    /// Fired after the RabbitMQ consumer confirms ScyllaDB persistence.
    /// Sent to everyone in the channel group — including the original sender.
    /// The sender should replace its optimistic local message with this authoritative copy.
    /// </summary>
    Task MessageReceived(MessageResponse message);

    /// <summary>
    /// Fired after a message is soft-deleted and confirmed in ScyllaDB.
    /// Clients redact the message content locally on receipt.
    /// </summary>
    Task MessageDeleted(MessageDeletedPayload payload);

    /// <summary>
    /// Fired after a message edit is confirmed in ScyllaDB.
    /// Clients patch the existing message content locally on receipt.
    /// </summary>
    Task MessageEdited(MessageEditedPayload payload);

    /// <summary>
    /// Fired when channel metadata changes (create / update / reorder).
    /// Clients refresh their channel list on receipt.
    /// Distinct from ChannelDeleted — clients should update, not remove.
    /// </summary>
    Task ChannelUpdated(ChannelResponse channel);

    /// <summary>
    /// Fired when a channel is permanently deleted.
    /// Clients must remove the channel from the sidebar and navigate away
    /// if the user is currently viewing it.
    /// Distinct from ChannelUpdated — clients must remove, not update.
    /// </summary>
    Task ChannelDeleted(ChannelDeletedPayload payload);

    /// <summary>
    /// Fired when a user's unread count for a channel changes (new message they
    /// didn't send, or a mark-as-read reset to zero). Sent per-user via
    /// Clients.User — reaches all of that user's connections for multi-device sync.
    /// Carries the absolute count, not a delta, so the client is self-correcting.
    /// </summary>
    Task UnreadCountUpdated(UnreadCountPayload payload);

    /// <summary>
    /// Fired when the consumer's Scylla persist fails after all retries are exhausted.
    /// Sent only to the original sender via Clients.User — other users never saw an
    /// optimistic copy and must not receive this event.
    /// The client should remove the optimistic message and show an error state.
    /// </summary>
    Task MessageFailed(MessageFailedPayload payload);

    /// <summary>
    /// Fired when a user's first connection comes online. Sent to that user's friends
    /// via Clients.User (per-recipient) — recipient resolution is a seam that returns
    /// no one until the friends-system feature lands.
    /// </summary>
    Task OnlineStatus(OnlineStatusPayload payload);

    /// <summary>
    /// Fired when a user's last connection drops (all tabs/devices closed). Same
    /// per-recipient delivery and friends-system seam as OnlineStatus.
    /// </summary>
    Task OfflineStatus(OfflineStatusPayload payload);

    /// <summary>
    /// Fired when a connected user's status changes (manual status pick or the
    /// 15-min idle toggle). Friends receive the public effective status; the user's
    /// own connections receive their raw preferred status (so other tabs sync the
    /// real choice — including invisible/dnd, which friends never see as such).
    /// </summary>
    Task StatusChanged(StatusChangedPayload payload);

    /// <summary>
    /// Fired when one of the user's mutes ends — either swept by MuteExpiryService
    /// once its expiry passes, or removed by a manual unmute. Sent only to the mute's
    /// owner via Clients.User so all their tabs drop the mute from local state.
    /// </summary>
    Task MuteExpired(MuteExpiredPayload payload);
}

/// <summary>Minimal delete notification — no content, just identity.</summary>
public record MessageDeletedPayload(
    long MessageId,
    long ChannelId,
    long GuildId,
    long DeletedByUserId,
    long DeletedAt
);

/// <summary>Minimal edit notification — new content and metadata only.</summary>
public record MessageEditedPayload(
    long MessageId,
    long ChannelId,
    long GuildId,
    long EditedByUserId,
    string NewContent,
    long EditedAt
);

/// <summary>
/// Channel deletion notification sent to all guild group subscribers.
/// Carries enough context for the client to navigate away if needed.
/// </summary>
public record ChannelDeletedPayload(long ChannelId, long GuildId, long DeletedAt);

/// <summary>Absolute unread count for one user in one channel.</summary>
public record UnreadCountPayload(long ChannelId, long GuildId, int UnreadCount);

/// <summary>Failure notification sent to the original sender of an undeliverable message.</summary>
public record MessageFailedPayload(long MessageId, long ChannelId, long GuildId);

/// <summary>A user came online (their first connection was established).</summary>
public record OnlineStatusPayload(long UserId, string Status);

/// <summary>A user went offline (their last connection dropped).</summary>
public record OfflineStatusPayload(long UserId);

/// <summary>
/// A connected user's status changed. <see cref="Status"/> carries either the public
/// effective status (to friends) or the raw preferred status (to the user's own tabs),
/// depending on the audience the broadcaster targeted.
/// </summary>
public record StatusChangedPayload(long UserId, string Status, string? StatusMessage);

/// <summary>A mute the user held has ended (expired or manually removed).</summary>
public record MuteExpiredPayload(long TargetId, string TargetType);
