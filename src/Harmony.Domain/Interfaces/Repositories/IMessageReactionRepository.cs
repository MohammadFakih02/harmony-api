using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

/// <summary>
/// Reaction storage for messages (PostgreSQL). Reactions have a page-aggregate access pattern
/// (emoji → count + "did I react" across a window of messages) that one indexed relational query
/// serves far better than N per-message Scylla partition reads — hence they live here, not in Scylla.
/// </summary>
public interface IMessageReactionRepository
{
    /// <summary>
    /// Adds a reaction. Idempotent: the (message, emoji, user) PK means re-reacting is a harmless
    /// no-op (ON CONFLICT DO NOTHING), mirroring pin idempotency.
    /// </summary>
    Task AddAsync(
        long messageId,
        long channelId,
        string emoji,
        long userId,
        long createdAt,
        CancellationToken ct = default
    );

    /// <summary>Removes one user's reaction with the given emoji. A no-op if absent.</summary>
    Task RemoveAsync(long messageId, string emoji, long userId, CancellationToken ct = default);

    /// <summary>
    /// Aggregates reactions for a batch of messages in one query: per message id, a list of
    /// <see cref="ReactionSummary"/> (emoji, total count, whether <paramref name="viewerId"/> reacted),
    /// ordered by first-reaction time so pill order is stable. Messages with no reactions are absent
    /// from the dictionary.
    /// </summary>
    Task<Dictionary<long, List<ReactionSummary>>> GetSummariesAsync(
        IEnumerable<long> messageIds,
        long viewerId,
        CancellationToken ct = default
    );
}
