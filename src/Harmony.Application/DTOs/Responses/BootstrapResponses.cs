namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// Everything the client needs at boot, aggregated into one round trip
/// (GET /api/users/me/bootstrap) instead of the nine individual requests it replaces.
/// Each field mirrors the corresponding standalone endpoint's response shape exactly —
/// the per-feature endpoints remain the refresh / fallback paths.
/// </summary>
public record BootstrapResponse(
    UserProfileResponse Profile,
    IEnumerable<GuildResponse> Guilds,
    IEnumerable<UnreadCountResponse> Unread,
    IEnumerable<FriendResponse> Friends,
    IEnumerable<PendingFriendResponse> PendingFriends,
    IEnumerable<DirectMessageChannelResponse> Dms,
    Dictionary<string, string> Nicknames,
    IEnumerable<NotificationResponse> Notifications,
    int NotificationUnreadCount
);
