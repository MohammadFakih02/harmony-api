using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harmony.Application.Exceptions;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.RabbitMQ;

public class MessageConsumerHandler : IMessageConsumerHandler
{
    private readonly IMessageRepository _messageRepository;
    private readonly INotificationService _notificationService;
    private readonly IPushOutboxRepository _pushOutbox;
    private readonly IPushDispatchNudge _pushNudge;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly ILogger<MessageConsumerHandler> _logger;

    public MessageConsumerHandler(
        IMessageRepository messageRepository,
        INotificationService notificationService,
        IPushOutboxRepository pushOutboxRepository,
        IPushDispatchNudge pushNudge,
        ISnowflakeIdGenerator snowflake,
        ILogger<MessageConsumerHandler> logger
    )
    {
        _messageRepository = messageRepository;
        _notificationService = notificationService;
        _pushOutbox = pushOutboxRepository;
        _pushNudge = pushNudge;
        _snowflake = snowflake;
        _logger = logger;
    }

    public async Task HandleMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Handling MessageSent — MessageId: {MessageId}, ChannelId: {ChannelId}",
            evt.MessageId,
            evt.ChannelId
        );

        var message = new Message
        {
            MessageId = evt.MessageId,
            ChannelId = evt.ChannelId,
            UserId = evt.UserId,
            Content = evt.Content,
            AttachmentIds = evt.AttachmentIds,
            MentionIds = evt.MentionIds,
            ReplyToId = evt.ReplyToId,
            IsDeleted = false,
            IsEdited = false,
            MessageType = evt.MessageType,
        };

        await _messageRepository.SaveAsync(message, ct);

        // Best-effort, like the consumer's unread fan-out: the Scylla message is already
        // persisted, so a notification-write failure (e.g. a constraint violation because a
        // mentioned user/channel is gone) must NOT bubble into the retry pipeline and block
        // the message's broadcast. Notifications are a non-critical side effect.
        if (evt.MentionIds.Count > 0)
        {
            try
            {
                await _notificationService.CreateMentionNotificationsAsync(
                    evt.MentionIds,
                    evt.UserId,
                    evt.GuildId,
                    evt.ChannelId,
                    evt.MessageId,
                    evt.SentAt.ToUnixTimeMilliseconds(),
                    ct
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "MessageSent: mention-notification creation failed for MessageId {MessageId} — message persisted, continuing",
                    evt.MessageId
                );
            }
        }

        // Reply notification — same best-effort posture. The replied-to author is resolved from
        // the already-persisted messages_by_id row; skipped when they're also @mentioned in this
        // message (the mention notification above already covers them — no double ping).
        long? replyRecipientId = null;
        if (evt.ReplyToId is { } replyToId)
        {
            try
            {
                var original = await _messageRepository.GetByIdAsync(replyToId, ct);
                if (
                    original is not null
                    && !original.IsDeleted
                    && original.UserId != evt.UserId
                    && !evt.MentionIds.Contains(original.UserId)
                )
                {
                    replyRecipientId = original.UserId;
                    await _notificationService.CreateReplyNotificationAsync(
                        original.UserId,
                        evt.UserId,
                        evt.GuildId,
                        evt.ChannelId,
                        evt.MessageId,
                        evt.SentAt.ToUnixTimeMilliseconds(),
                        ct
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "MessageSent: reply-notification creation failed for MessageId {MessageId} — message persisted, continuing",
                    evt.MessageId
                );
            }
        }

        // DM offline-push intent — same best-effort posture. One outbox row per DM/group
        // message; the dispatcher fans out to the channel's participants (minus the sender
        // and anyone already covered by a mention/reply push above) and applies the
        // offline/preference/mute/block gates at send time. No Notification row exists for
        // plain DM messages — this is offline delivery only. System notices (group join/
        // leave) are not pushable content — only real "text" messages stage a row.
        if (evt.GuildId is null && evt.MessageType == "text")
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var excludes = evt.MentionIds.ToList();
                if (replyRecipientId is { } replied)
                    excludes.Add(replied);

                await _pushOutbox.AddAsync(
                    new PushOutboxMessage
                    {
                        Id = _snowflake.NextId(),
                        Kind = PushKind.Dm,
                        RecipientId = 0,
                        ActorId = evt.UserId,
                        ChannelId = evt.ChannelId,
                        MessageId = evt.MessageId,
                        ExcludeUserIds = excludes.Count > 0 ? string.Join(',', excludes) : null,
                        NextAttemptAt = now,
                        CreatedAt = now,
                    }
                );
                await _pushOutbox.SaveChangesAsync();
                _pushNudge.Signal();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "MessageSent: DM push-outbox staging failed for MessageId {MessageId} — message persisted, continuing",
                    evt.MessageId
                );
            }
        }

        _logger.LogInformation(
            "MessageSent handled (Scylla) — MessageId: {MessageId}",
            evt.MessageId
        );
    }

    public async Task HandleMessageDeletedAsync(
        MessageDeletedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("Handling MessageDeleted — MessageId: {MessageId}", evt.MessageId);

        var existing = await _messageRepository.GetByIdAsync(evt.MessageId, ct);
        if (existing is null || existing.IsDeleted)
        {
            _logger.LogDebug(
                "MessageDeleted skipped — already deleted or not found: {MessageId}",
                evt.MessageId
            );
            return;
        }

        await _messageRepository.DeleteAsync(evt.MessageId, evt.ChannelId, ct);

        _logger.LogInformation(
            "MessageDeleted handled (Scylla) — MessageId: {MessageId}",
            evt.MessageId
        );
    }

    public async Task HandleMessageEditedAsync(
        MessageEditedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("Handling MessageEdited — MessageId: {MessageId}", evt.MessageId);

        var existing = await _messageRepository.GetByIdAsync(evt.MessageId, ct);
        if (existing is null)
        {
            // Out-of-order: the MessageSent event hasn't been persisted yet (broker reordering,
            // or Sent is still mid-retry). Signal the consumer to back off and requeue so the
            // insert lands first — same pattern as SearchIndexConsumer. A blind upsert here would
            // create a partial row (no user_id/type/attachments) AND be clobbered by the
            // later-arriving Sent, because Scylla LWW uses wall-clock write time, not the logical
            // event time.
            throw new ServiceUnavailableException(
                $"MessageEdited {evt.MessageId} arrived before its MessageSent — requeuing"
            );
        }
        if (existing.IsDeleted)
        {
            _logger.LogDebug(
                "MessageEdited skipped — message already deleted: {MessageId}",
                evt.MessageId
            );
            return;
        }

        await _messageRepository.EditAsync(evt.MessageId, evt.ChannelId, evt.NewContent, evt.MentionIds, ct);

        // Re-detection notifies only NEWLY added mentions — an already-mentioned user is not
        // re-notified, and removing a mention never un-notifies. The "old" set comes from the
        // event (captured by MessageService before its synchronous edit); re-reading it here
        // would return the already-overwritten new set. Best-effort, same as the send-path:
        // the Scylla edit is already persisted, so a notification failure must not bubble.
        var newlyMentioned = evt.MentionIds.Except(evt.OldMentionIds).ToList();
        if (newlyMentioned.Count > 0)
        {
            try
            {
                await _notificationService.CreateMentionNotificationsAsync(
                    newlyMentioned,
                    evt.EditedByUserId,
                    evt.GuildId,
                    evt.ChannelId,
                    evt.MessageId,
                    evt.EditedAt.ToUnixTimeMilliseconds(),
                    ct
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "MessageEdited: mention-notification creation failed for MessageId {MessageId} — message persisted, continuing",
                    evt.MessageId
                );
            }
        }

        _logger.LogInformation(
            "MessageEdited handled (Scylla) — MessageId: {MessageId}",
            evt.MessageId
        );
    }

    public async Task HandleChannelDeletedAsync(
        ChannelDeletedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug(
            "Handling ChannelDeleted — Purging ScyllaDB Partitions for ChannelId: {ChannelId}",
            evt.ChannelId
        );

        // Execute atomic NoSQL partition tombstone purges [14]
        await _messageRepository.PurgeChannelPartitionsAsync(evt.ChannelId, ct);

        _logger.LogInformation(
            "ChannelDeleted handled (Scylla Purges) — ChannelId: {ChannelId}",
            evt.ChannelId
        );
    }
}
