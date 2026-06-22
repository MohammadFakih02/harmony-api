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
