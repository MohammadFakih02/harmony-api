namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// A persisted notification row, as returned by GET /api/notifications. No UserId field —
/// the list endpoint only ever returns the caller's own rows, so it would be redundant
/// (matches NotificationPayload, the live-push counterpart, which omits it for the same
/// reason). Id is kept, unlike the payload: it's needed as the mark-read route param and
/// as a stable list-rendering key, neither of which a just-created push event needs.
/// </summary>
public record NotificationResponse(
    long Id,
    string Type,
    long ActorId,
    long? GuildId,
    long? ChannelId,
    long? MessageId,
    bool IsRead,
    long CreatedAt
);

/// <summary>
/// The caller's notification settings for one guild: the resolved guild-scope level (defaulting to
/// "mentions" when unset) plus only the channels for which an explicit per-channel override exists.
/// A channel absent from <see cref="Channels"/> inherits the guild level.
/// </summary>
public record GuildNotificationSettingsResponse(
    string GuildLevel,
    IEnumerable<ChannelNotificationSettingResponse> Channels
);

public record ChannelNotificationSettingResponse(long ChannelId, string Level);
