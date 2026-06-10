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
