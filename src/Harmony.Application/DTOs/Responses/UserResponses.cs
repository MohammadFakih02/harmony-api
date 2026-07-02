namespace Harmony.Application.DTOs.Responses;

// Returned for /api/users/me — includes private fields
public record UserProfileResponse(
    long Id,
    string Username,
    string Email,
    string? AvatarKey,
    string? BannerKey,
    string? Bio,
    string? StatusMessage,
    long? StatusMessageExpiresAt,
    string PreferredStatus,
    long? PreferredStatusExpiresAt,
    string AccountStatus,
    long CreatedAt,
    // ISO "yyyy-MM-dd" — private; only the owner sees their raw DOB (others see Age below).
    string? DateOfBirth,
    // "everyone" | "friends_only" — who may open a new DM with the owner. Private setting.
    string DmPrivacy
);

// Returned per user from /api/users/presence — effective status + (when visible) custom message
public record UserPresenceResponse(string Status, string? StatusMessage);

// Returned for /api/users/{id} — public fields only, no email
public record PublicUserResponse(
    long Id,
    string Username,
    string? AvatarKey,
    string? BannerKey,
    string? Bio,
    string? StatusMessage,
    // Computed years from DOB (not the raw date — others don't see your birthday); null = unset.
    int? Age,
    // "everyone" | "friends_only" — lets the client hide the DM/Message action for a stranger when
    // the target only accepts DMs from friends (the server still enforces on send regardless).
    string DmPrivacy
);

// Returned for /api/users/me/blocks — the blocked user's public identity + when blocked
public record BlockResponse(
    long Id,
    string Username,
    string? AvatarKey,
    string? BannerKey,
    long CreatedAt
);

// Returned for /api/mutes — one active mute the caller holds
public record MuteResponse(
    string TargetType,
    long TargetId,
    long? MutedUntil,
    long CreatedAt
);

// Returned for /api/notifications/preferences — the caller's notification toggles.
// A user with no row yet (registered before the feature, or never saved) reads all-true defaults.
public record NotificationPreferenceResponse(
    bool MentionsEnabled,
    bool RepliesEnabled,
    bool FriendRequests,
    bool GuildInvites,
    bool PushEnabled
);