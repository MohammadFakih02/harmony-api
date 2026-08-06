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

/// <summary>
/// Moves a channel into a category, or out to top-level when CategoryId is null. A dedicated
/// endpoint (rather than folding into UpdateChannelRequest) because that request's "null = don't
/// change" convention can't express "clear the category" — this one always sets what's provided.
/// </summary>
public record MoveChannelCategoryRequest(long? CategoryId);

public record UpsertChannelOverrideRequest(
    string TargetType,      // "role" | "user"
    long AllowBits,
    long DenyBits
);