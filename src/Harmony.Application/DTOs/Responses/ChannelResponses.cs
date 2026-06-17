namespace Harmony.Application.DTOs.Responses;

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

public record ChannelOverrideResponse(
    long Id,
    long ChannelId,
    long TargetId,
    string TargetType,
    long AllowBits,
    long DenyBits
);