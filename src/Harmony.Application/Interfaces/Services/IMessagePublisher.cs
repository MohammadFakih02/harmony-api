using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harmony.Domain.Interfaces;

public interface IMessagePublisher
{
    Task PublishMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default);
    Task PublishMessageDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default);
    Task PublishMessageEditedAsync(MessageEditedEvent evt, CancellationToken ct = default);

    /// <summary>Publishes an asynchronous channel deletion event to RabbitMQ [12].</summary>
    Task PublishChannelDeletedAsync(ChannelDeletedEvent evt, CancellationToken ct = default);
}

public record MessageSentEvent(
    long MessageId,
    long ChannelId,
    long GuildId,
    long UserId,
    string Content,
    string MessageType,
    List<long> AttachmentIds,
    List<long> MentionIds,
    long? ReplyToId,
    DateTimeOffset SentAt
);

public record MessageDeletedEvent(
    long MessageId,
    long ChannelId,
    long GuildId,
    long DeletedByUserId,
    DateTimeOffset DeletedAt
);

public record MessageEditedEvent(
    long MessageId,
    long ChannelId,
    long GuildId,
    long EditedByUserId,
    string NewContent,
    DateTimeOffset EditedAt
);

/// <summary>Asynchronous envelope representing a deleted channel [12].</summary>
public record ChannelDeletedEvent(long ChannelId, long GuildId, DateTimeOffset DeletedAt);
