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
    /// Full-text search within a single guild, newest first. Optional operator filters:
    /// <paramref name="channelId"/> narrows to one channel, <paramref name="authorId"/> to one sender,
    /// <paramref name="after"/> is an inclusive lower <c>created_at</c> bound, and
    /// <paramref name="hasLink"/> keeps only messages containing a URL. An empty
    /// <paramref name="query"/> matches every row (the filters do the narrowing). <paramref name="before"/>
    /// is a keyset cursor (<c>created_at</c>, exclusive) for "load more". Returns up to
    /// <paramref name="limit"/> raw rows — permission filtering happens above.
    /// </summary>
    Task<List<MessageSearch>> SearchAsync(
        long guildId,
        string query,
        long? channelId,
        long? authorId,
        long? after,
        bool hasLink,
        long? before,
        int limit,
        CancellationToken ct = default
    );

    /// <summary>
    /// Full-text search within a single channel (guild channel or DM), newest first, regardless of
    /// guild. Optional operator filters mirror <see cref="SearchAsync"/> (<paramref name="authorId"/>,
    /// <paramref name="after"/>, <paramref name="hasLink"/>); an empty <paramref name="query"/> matches
    /// every row. <paramref name="before"/> is a keyset cursor (<c>created_at</c>, exclusive) for "load
    /// more". Authorization is the caller's responsibility — a DM caller must be a participant.
    /// </summary>
    Task<List<MessageSearch>> SearchChannelAsync(
        long channelId,
        string query,
        long? authorId,
        long? after,
        bool hasLink,
        long? before,
        int limit,
        CancellationToken ct = default
    );
}
