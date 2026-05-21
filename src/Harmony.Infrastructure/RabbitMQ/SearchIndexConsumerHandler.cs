using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.RabbitMQ;

public class SearchIndexConsumerHandler
{
    private readonly HarmonyDbContext _db;
    private readonly ILogger<SearchIndexConsumerHandler> _logger;

    public SearchIndexConsumerHandler(
        HarmonyDbContext db,
        ILogger<SearchIndexConsumerHandler> logger
    )
    {
        _db = db;
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
            _logger.LogDebug("Search index delete skipped — not found: {MessageId}", evt.MessageId);
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
            _logger.LogDebug("Search index edit skipped — not found: {MessageId}", evt.MessageId);
            return;
        }

        entry.Content = evt.NewContent;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "MessageEdited updated in index — MessageId: {MessageId}",
            evt.MessageId
        );
    }
}
