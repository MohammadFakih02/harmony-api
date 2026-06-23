using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.RabbitMQ;

public class MessageConsumerHandler : IMessageConsumerHandler
{
    private readonly IMessageRepository _messageRepository;
    private readonly INotificationService _notificationService;
    private readonly ILogger<MessageConsumerHandler> _logger;

    public MessageConsumerHandler(
        IMessageRepository messageRepository,
        INotificationService notificationService,
        ILogger<MessageConsumerHandler> logger
    )
    {
        _messageRepository = messageRepository;
        _notificationService = notificationService;
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
        if (existing is null || existing.IsDeleted)
        {
            _logger.LogDebug(
                "MessageEdited skipped — deleted or not found: {MessageId}",
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
