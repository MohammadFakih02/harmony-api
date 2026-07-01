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
}
