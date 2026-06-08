using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Hubs;

/// <summary>
/// Strongly-typed contract for all server → client SignalR push events.
///
/// Lives in Harmony.Application so both Harmony.API (ChatHub) and
/// Harmony.Infrastructure (ScyllaMessageConsumer broadcast) can reference it
/// without creating a circular dependency.
///
/// Rules:
///   - Every method must return Task.
///   - Method names are the string keys Angular's SignalR client subscribes to.
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
    /// Fired to all connections in a guild group when channel metadata changes
    /// (create / update / delete / reorder). Clients refresh their channel list on receipt.
    /// </summary>
    Task ChannelUpdated(ChannelResponse channel);
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
