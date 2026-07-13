using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Full-text message search scoped to a guild the caller belongs to. Results are filtered to the
/// channels the caller can <c>ViewChannel</c> (override-hidden channels never leak).
/// </summary>
public interface ISearchService
{
    Task<SearchResultsResponse> SearchGuildAsync(
        long userId,
        long guildId,
        string query,
        long? channelId,
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
        long? before,
        CancellationToken ct = default
    );
}
