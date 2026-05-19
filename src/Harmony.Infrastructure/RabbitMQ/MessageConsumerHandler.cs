using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.RabbitMQ;

public class MessageConsumerHandler : IMessageConsumerHandler
{
    private readonly IMessageRepository _messageRepository;
    private readonly HarmonyDbContext _db;
    private readonly ILogger<MessageConsumerHandler> _logger;

    public MessageConsumerHandler(
        IMessageRepository messageRepository,
        HarmonyDbContext db,
        ILogger<MessageConsumerHandler> logger
    )
    {
        _messageRepository = messageRepository;
        _db = db;
        _logger = logger;
    }

    public async Task HandleMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Handling MessageSent — MessageId: {MessageId}, ChannelId: {ChannelId}",
            evt.MessageId,
            evt.ChannelId
        );

        // 1 — Persist to ScyllaDB
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

        // 2 — Dual-write to PostgreSQL MessagesSearch
        var alreadyExists = await _db.MessagesSearch.AnyAsync(
            m => m.MessageId == evt.MessageId,
            ct
        );

        if (!alreadyExists)
        {
            _db.MessagesSearch.Add(
                new MessageSearch
                {
                    MessageId = evt.MessageId,
                    ChannelId = evt.ChannelId,
                    GuildId = evt.GuildId,
                    UserId = evt.UserId,
                    Content = evt.Content,
                    CreatedAt = evt.SentAt.ToUnixTimeMilliseconds(),
                }
            );

            await _db.SaveChangesAsync(ct);
        }

        // 3 — Parse mentions and create notifications
        if (evt.MentionIds.Count > 0)
            await CreateMentionNotificationsAsync(evt, ct);

        _logger.LogInformation("MessageSent handled — MessageId: {MessageId}", evt.MessageId);
    }

    public async Task HandleMessageDeletedAsync(
        MessageDeletedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("Handling MessageDeleted — MessageId: {MessageId}", evt.MessageId);

        // Soft delete in ScyllaDB
        await _messageRepository.DeleteAsync(evt.MessageId, evt.ChannelId, ct);

        // Remove from search index
        var searchEntry = await _db.MessagesSearch.FirstOrDefaultAsync(
            m => m.MessageId == evt.MessageId,
            ct
        );

        if (searchEntry is not null)
        {
            _db.MessagesSearch.Remove(searchEntry);
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("MessageDeleted handled — MessageId: {MessageId}", evt.MessageId);
    }

    public async Task HandleMessageEditedAsync(
        MessageEditedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("Handling MessageEdited — MessageId: {MessageId}", evt.MessageId);

        // Update content in ScyllaDB
        await _messageRepository.EditAsync(evt.MessageId, evt.ChannelId, evt.NewContent, ct);

        // Update search index
        var searchEntry = await _db.MessagesSearch.FirstOrDefaultAsync(
            m => m.MessageId == evt.MessageId,
            ct
        );

        if (searchEntry is not null)
        {
            searchEntry.Content = evt.NewContent;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("MessageEdited handled — MessageId: {MessageId}", evt.MessageId);
    }

    // --- Private helpers ---

    private async Task CreateMentionNotificationsAsync(MessageSentEvent evt, CancellationToken ct)
    {
        // Load notification preferences for all mentioned users in one query
        var preferences = await _db
            .NotificationPreferences.Where(p => evt.MentionIds.Contains(p.UserId))
            .ToListAsync(ct);

        var preferenceMap = preferences.ToDictionary(p => p.UserId);

        var notifications = new List<Notification>();

        foreach (var mentionedUserId in evt.MentionIds)
        {
            // Skip if user has mentions disabled
            if (preferenceMap.TryGetValue(mentionedUserId, out var pref) && !pref.MentionsEnabled)
                continue;

            // Skip self-mentions
            if (mentionedUserId == evt.UserId)
                continue;

            notifications.Add(
                new Notification
                {
                    Id = evt.MessageId + mentionedUserId, // deterministic — dedup safe
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

        // Skip any that already exist — idempotent
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
