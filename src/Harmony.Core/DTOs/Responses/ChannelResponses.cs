namespace Harmony.Core.DTOs.Responses;

public record ChannelResponse(
    long Id,
    long? GuildId,
    string Name,
    string Type,
    string? Topic,
    int Position,
    long? CategoryId,
    bool IsNsfw,
    int SlowmodeSeconds,
    int? Bitrate,
    int? UserLimit,
    long CreatedAt
);