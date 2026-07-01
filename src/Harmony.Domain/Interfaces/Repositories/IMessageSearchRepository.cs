using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

/// <summary>
/// Read access to the <c>MessagesSearch</c> full-text read model (maintained by the
/// SearchIndexConsumer). Full-text matching runs against the generated <c>content_search</c>
/// tsvector column via a parameterized <c>plainto_tsquery</c>.
/// </summary>
public interface IMessageSearchRepository
{
    /// <summary>
    /// Full-text search within a single guild, newest first. <paramref name="channelId"/> narrows to
    /// one channel; <paramref name="before"/> is a keyset cursor (<c>created_at</c>, exclusive) for
    /// "load more". Returns up to <paramref name="limit"/> raw rows — permission filtering happens above.
    /// </summary>
    Task<List<MessageSearch>> SearchAsync(
        long guildId,
        string query,
        long? channelId,
        long? before,
        int limit,
        CancellationToken ct = default
    );
}
