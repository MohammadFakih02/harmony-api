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

        // Persist to ScyllaDB only — Postgres search index is handled by SearchIndexConsumerHandler
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

        // Parse mentions and create notifications
        if (evt.MentionIds.Count > 0)
            await CreateMentionNotificationsAsync(evt, ct);

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

        // Idempotency check — skip if already deleted
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

        // Idempotency check — skip if deleted
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
