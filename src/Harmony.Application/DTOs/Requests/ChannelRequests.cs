namespace Harmony.Application.DTOs.Requests;

public record CreateChannelRequest(
    string Name,
    string Type,            // "text" | "voice" | "category"
    string? Topic,
    int Position,
    long? CategoryId,
    bool IsNsfw,
    int SlowmodeSeconds,
    int? Bitrate,           // voice only
    int? UserLimit          // voice only
);

public record UpdateChannelRequest(
    string? Name,
    string? Topic,
    bool? IsNsfw,
    int? SlowmodeSeconds,
    int? Bitrate,
    int? UserLimit,
    long? CategoryId
);

public record ReorderChannelRequest(
    long ChannelId,
    int Position
);

public record UpsertChannelOverrideRequest(
    string TargetType,      // "role" | "user"
    long AllowBits,
    long DenyBits
);