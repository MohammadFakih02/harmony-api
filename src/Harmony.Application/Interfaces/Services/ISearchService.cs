using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Optional search-operator filters parsed from the client's query (<c>from:</c>, <c>in:</c>,
/// <c>after:</c>, <c>has:link</c>). All are additive narrowings applied on top of the free-text match;
/// none can widen scope beyond the guild/channel the caller is already authorized to search, and every
/// hit still passes the per-row <c>ViewChannel</c> filter, so a forged id can only shrink the result set.
/// </summary>
public sealed record SearchFilters(
    long? ChannelId = null,
    long? AuthorId = null,
    long? After = null,
    bool HasLink = false
);

/// <summary>
/// Full-text message search scoped to a guild the caller belongs to. Results are filtered to the
/// channels the caller can <c>ViewChannel</c> (override-hidden channels never leak).
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Full-text search across a guild's channels the caller can <c>ViewChannel</c>. <paramref name="before"/>
    /// is an optional message-id cursor for paging older results.
    /// </summary>
    Task<SearchResultsResponse> SearchGuildAsync(
        long userId,
        long guildId,
        string query,
        SearchFilters filters,
        long? before,
        CancellationToken ct = default
    );

    /// <summary>
    /// Full-text search within a single DM/group-DM channel the caller participates in. Guild-less;
    /// authorization is a participant check on the channel (no per-channel visibility, unlike a guild).
    /// </summary>
    Task<SearchResultsResponse> SearchDmChannelAsync(
        long userId,
        long channelId,
        string query,
        SearchFilters filters,
        long? before,
        CancellationToken ct = default
    );
}
