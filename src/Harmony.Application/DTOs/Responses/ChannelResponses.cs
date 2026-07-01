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

/// <summary>
/// The authenticated caller's effective capabilities in a channel. Computed server-side so the
/// client never reasons about permission bits. <c>CanSend</c> already accounts for the member
/// timeout (which the cached permission resolver deliberately omits).
/// </summary>
public record ChannelCapabilitiesResponse(
    bool CanView,
    bool CanSend,
    bool CanAttach,
    bool CanManageMessages,
    bool CanManageChannels,
    bool CanPin,
    bool TimedOut
);