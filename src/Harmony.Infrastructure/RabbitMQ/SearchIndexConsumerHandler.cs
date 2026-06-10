using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harmony.Application.Exceptions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.RabbitMQ;

public class SearchIndexConsumerHandler
{
    private readonly HarmonyDbContext _db;
    private readonly IMessageRepository _messageRepository;
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
            var scyllaMessage = await _messageRepository.GetByIdAsync(evt.MessageId, ct);
            if (scyllaMessage is null)
            {
                throw new ServiceUnavailableException(
                    $"Message {evt.MessageId} not yet present in primary storage. Requeuing deletion event."
                );
            }

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
                throw new ServiceUnavailableException(
                    $"Message {evt.MessageId} not yet present in primary storage. Requeuing edit event."
                );
            }

            if (scyllaMessage.IsDeleted)
            {
                _logger.LogInformation(
                    "MessageEdited skipped — message is deleted in Scylla: {MessageId}",
                    evt.MessageId
                );
                return;
            }

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

    public async Task HandleChannelDeletedAsync(
        ChannelDeletedEvent evt,
        CancellationToken ct = default
    )
    {
        _logger.LogInformation(
            "Asynchronously cleaning up search index for deleted ChannelId: {ChannelId}",
            evt.ChannelId
        );

        // Decoupled relational cleanup — compiles into a single direct delete command [12]
        var deletedCount = await _db
            .MessagesSearch.Where(m => m.ChannelId == evt.ChannelId)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "Successfully pruned {Count} orphaned search index entries for ChannelId: {ChannelId}",
            deletedCount,
            evt.ChannelId
        );
    }
}
