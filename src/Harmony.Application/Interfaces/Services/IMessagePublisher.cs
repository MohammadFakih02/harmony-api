using System;
using System.Threading;
using System.Threading.Tasks;
using Harmony.Domain.Domain.Entities;

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
    long? GuildId,
    long UserId,
    string Content,
    string MessageType,
    List<long> AttachmentIds,
    List<long> MentionIds,
    long? ReplyToId,
    DateTimeOffset SentAt,
    // The subset of MentionIds that were reached ONLY via @everyone/@here (never a direct @user
    // or @role mention) — the consumer uses this so a per-scope suppress-@everyone opt-out can
    // drop the broadcast ping without touching direct mentions. Defaulted so in-flight events
    // from a pre-upgrade producer deserialize as "no @everyone-origin recipients".
    List<long>? EveryoneMentionIds = null,
    // Server-built forward snapshot when this message is a forward (§Slice 4). Defaulted so
    // in-flight events from a pre-upgrade producer deserialize as "not a forward".
    MessageForwardSnapshot? Forward = null,
    // Opaque client idempotency token, carried through to the broadcast MessageResponse so the
    // sender can dedupe its optimistic bubble. Not persisted. Defaulted for pre-upgrade events.
    string? Nonce = null
);

public record MessageDeletedEvent(
    long MessageId,
    long ChannelId,
    long? GuildId,
    long DeletedByUserId,
    DateTimeOffset DeletedAt
);

public record MessageEditedEvent(
    long MessageId,
    long ChannelId,
    long? GuildId,
    long EditedByUserId,
    string NewContent,
    List<long> MentionIds,
    // The mention set BEFORE this edit, captured by MessageService before it writes the new
    // one to Scylla — the consumer diffs against this to notify only newly-added mentions.
    // (It can't re-read the "old" set itself: the synchronous edit has already overwritten it.)
    List<long> OldMentionIds,
    DateTimeOffset EditedAt,
    // Same role as on MessageSentEvent: the @everyone/@here-only subset of MentionIds, so a
    // newly-added @everyone ping honours the suppress-@everyone opt-out on the edit path too.
    List<long>? EveryoneMentionIds = null
);

/// <summary>Asynchronous envelope representing a deleted channel [12].</summary>
public record ChannelDeletedEvent(long ChannelId, long GuildId, DateTimeOffset DeletedAt);
