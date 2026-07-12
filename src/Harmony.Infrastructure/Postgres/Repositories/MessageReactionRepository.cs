using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class MessageReactionRepository : IMessageReactionRepository
{
    private readonly HarmonyDbContext _db;

    public MessageReactionRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(
        long messageId,
        long channelId,
        string emoji,
        long userId,
        long createdAt,
        CancellationToken ct = default
    )
    {
        // Idempotent insert: the (message, emoji, user) PK makes a duplicate reaction a no-op.
        // ON CONFLICT DO NOTHING avoids a check-then-insert race under concurrent double-clicks.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""MessageReactions"" (message_id, channel_id, emoji, user_id, created_at)
               VALUES ({messageId}, {channelId}, {emoji}, {userId}, {createdAt})
               ON CONFLICT (message_id, emoji, user_id) DO NOTHING",
            ct
        );
    }

    public async Task RemoveAsync(
        long messageId,
        string emoji,
        long userId,
        CancellationToken ct = default
    )
    {
        await _db
            .MessageReactions.Where(r =>
                r.MessageId == messageId && r.Emoji == emoji && r.UserId == userId
            )
            .ExecuteDeleteAsync(ct);
    }

    public async Task<Dictionary<long, List<ReactionSummary>>> GetSummariesAsync(
        IEnumerable<long> messageIds,
        long viewerId,
        CancellationToken ct = default
    )
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<long, List<ReactionSummary>>();

        // One grouped query: (message, emoji) → count + did-viewer-react + earliest reaction time
        // (for stable pill order). MinCreatedAt orders the pills; it never reaches the client.
        var rows = await _db
            .MessageReactions.AsNoTracking()
            .Where(r => ids.Contains(r.MessageId))
            .GroupBy(r => new { r.MessageId, r.Emoji })
            .Select(g => new
            {
                g.Key.MessageId,
                g.Key.Emoji,
                Count = g.Count(),
                MeReacted = g.Any(r => r.UserId == viewerId),
                FirstAt = g.Min(r => r.CreatedAt),
            })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.MessageId)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.OrderBy(r => r.FirstAt)
                        .Select(r => new ReactionSummary(r.Emoji, r.Count, r.MeReacted))
                        .ToList()
            );
    }
}
