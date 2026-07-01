namespace Harmony.Application.DTOs.Responses;

/// <summary>One full-text search hit: the message plus enough context to render and jump to it.</summary>
public record SearchResultResponse(
    long MessageId,
    long ChannelId,
    string ChannelName,
    long? GuildId,
    long UserId,
    string Username,
    string? AvatarKey,
    string Content,
    long CreatedAt
);

/// <summary>
/// A page of search results (newest first) plus a <see cref="HasMore"/> hint. Paginate by passing the
/// last result's <c>CreatedAt</c> back as the <c>before</c> cursor.
/// </summary>
public record SearchResultsResponse(IReadOnlyList<SearchResultResponse> Results, bool HasMore);
