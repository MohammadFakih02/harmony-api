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

/// <summary>A soft-deleted channel as shown in a guild's Trash (§5.71 #5) — just enough to identify
/// it and show how long it's been trashed (drives the "auto-deletes in N days" hint client-side).</summary>
public record DeletedChannelResponse(
    long Id,
    long? GuildId,
    string Name,
    string Type,
    long? DeletedAt
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
    bool CanReact,
    bool CanUseVideo,
    bool CanStream,
    bool TimedOut
);