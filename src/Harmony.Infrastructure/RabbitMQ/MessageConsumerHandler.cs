using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.RabbitMQ;

public class MessageConsumerHandler : IMessageConsumerHandler
{
    private readonly IMessageRepository _messageRepository;
    private readonly HarmonyDbContext _db;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly ILogger<MessageConsumerHandler> _logger;

    public MessageConsumerHandler(
        IMessageRepository messageRepository,
        HarmonyDbContext db,
        ISnowflakeIdGenerator snowflake,
        ILogger<MessageConsumerHandler> logger
    )
    {
        _messageRepository = messageRepository;
        _db = db;
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
                await CreateMentionNotificationsAsync(evt, ct);
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

        await _messageRepository.EditAsync(evt.MessageId, evt.ChannelId, evt.NewContent, ct);

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

    private async Task CreateMentionNotificationsAsync(MessageSentEvent evt, CancellationToken ct)
    {
        var preferences = await _db
            .NotificationPreferences.Where(p => evt.MentionIds.Contains(p.UserId))
            .ToListAsync(ct);

        var preferenceMap = preferences.ToDictionary(p => p.UserId);
        var notifications = new List<Notification>();

        foreach (var mentionedUserId in evt.MentionIds)
        {
            if (preferenceMap.TryGetValue(mentionedUserId, out var pref) && !pref.MentionsEnabled)
                continue;

            if (mentionedUserId == evt.UserId)
                continue;

            notifications.Add(
                new Notification
                {
                    Id = _snowflake.NextId(),
                    UserId = mentionedUserId,
                    Type = "mention",
                    ActorId = evt.UserId,
                    GuildId = evt.GuildId,
                    ChannelId = evt.ChannelId,
                    MessageId = evt.MessageId,
                    IsRead = false,
                    CreatedAt = evt.SentAt.ToUnixTimeMilliseconds(),
                }
            );
        }

        if (notifications.Count == 0)
            return;

        var existingIds = await _db
            .Notifications.Where(n => notifications.Select(x => x.Id).Contains(n.Id))
            .Select(n => n.Id)
            .ToListAsync(ct);

        var toInsert = notifications.Where(n => !existingIds.Contains(n.Id)).ToList();

        if (toInsert.Count > 0)
        {
            _db.Notifications.AddRange(toInsert);
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogDebug(
            "Created {Count} mention notifications for MessageId: {MessageId}",
            toInsert.Count,
            evt.MessageId
        );
    }
}
