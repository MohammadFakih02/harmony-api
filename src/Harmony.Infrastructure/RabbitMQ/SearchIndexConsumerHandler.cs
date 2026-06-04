using Harmony.Application.Exceptions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories; // Required namespace
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.RabbitMQ;

public class SearchIndexConsumerHandler
{
    private readonly HarmonyDbContext _db;
    private readonly IMessageRepository _messageRepository; // Inject primary message store
    private readonly ILogger<SearchIndexConsumerHandler> _logger;

    public SearchIndexConsumerHandler(
        HarmonyDbContext db,
        IMessageRepository messageRepository,
        ILogger<SearchIndexConsumerHandler> logger
    )
    {
        _db = db;
        _messageRepository = messageRepository;
        _logger = logger;
    }

    public async Task HandleMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default)
    {
        _logger.LogDebug("Indexing MessageSent — MessageId: {MessageId}", evt.MessageId);

        var alreadyExists = await _db.MessagesSearch.AnyAsync(
            m => m.MessageId == evt.MessageId,
            ct
        );

        if (alreadyExists)
        {
            _logger.LogDebug("Search index skipped — already exists: {MessageId}", evt.MessageId);
            return;
        }

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

        _logger.LogInformation("MessageSent indexed — MessageId: {MessageId}", evt.MessageId);
    }

    public async Task HandleMessageDeletedAsync(
        MessageDeletedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("Removing from search index — MessageId: {MessageId}", evt.MessageId);

        var entry = await _db.MessagesSearch.FirstOrDefaultAsync(
            m => m.MessageId == evt.MessageId,
            ct
        );

        if (entry is null)
        {
            // Verify if the message exists in primary storage
            var scyllaMessage = await _messageRepository.GetByIdAsync(evt.MessageId, ct);
            if (scyllaMessage is null)
            {
                // Out of order: Requeue and wait for insert
                throw new ServiceUnavailableException(
                    $"Message {evt.MessageId} not yet present in primary storage. Requeuing deletion event."
                );
            }

            // Idempotency: The message exists in ScyllaDB but is already deleted from Postgres FTS. Return success.
            _logger.LogInformation(
                "MessageDeleted skipped — search index already clean for MessageId: {MessageId}",
                evt.MessageId
            );
            return;
        }

        _db.MessagesSearch.Remove(entry);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "MessageDeleted removed from index — MessageId: {MessageId}",
            evt.MessageId
        );
    }

    public async Task HandleMessageEditedAsync(
        MessageEditedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogDebug("Updating search index — MessageId: {MessageId}", evt.MessageId);

        var entry = await _db.MessagesSearch.FirstOrDefaultAsync(
            m => m.MessageId == evt.MessageId,
            ct
        );

        if (entry is null)
        {
            var scyllaMessage = await _messageRepository.GetByIdAsync(evt.MessageId, ct);
            if (scyllaMessage is null)
            {
                // Out of order: Requeue and wait for insert
                throw new ServiceUnavailableException(
                    $"Message {evt.MessageId} not yet present in primary storage. Requeuing edit event."
                );
            }

            if (scyllaMessage.IsDeleted)
            {
                // Safe bypass: cannot edit an already deleted message
                _logger.LogInformation(
                    "MessageEdited skipped — message is deleted in Scylla: {MessageId}",
                    evt.MessageId
                );
                return;
            }

            // Out-of-order write lag: Message exists in Scylla but is missing in Postgres
            throw new ServiceUnavailableException(
                $"Message {evt.MessageId} present in Scylla but missing in Postgres FTS. Requeuing edit event."
            );
        }

        entry.Content = evt.NewContent;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "MessageEdited updated in index — MessageId: {MessageId}",
            evt.MessageId
        );
    }
}
