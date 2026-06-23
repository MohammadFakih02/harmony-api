namespace Harmony.Application.DTOs.Responses;

// Returned for /api/users/me — includes private fields
public record UserProfileResponse(
    long Id,
    string Username,
    string? Discriminator,
    string Email,
    string? AvatarKey,
    string? BannerKey,
    string? Bio,
    string? StatusMessage,
    long? StatusMessageExpiresAt,
    string PreferredStatus,
    long? PreferredStatusExpiresAt,
    string AccountStatus,
    long CreatedAt
);

// Returned per user from /api/users/presence — effective status + (when visible) custom message
public record UserPresenceResponse(string Status, string? StatusMessage);

// Returned for /api/users/{id} — public fields only, no email
public record PublicUserResponse(
    long Id,
    string Username,
    string? Discriminator,
    string? AvatarKey,
    string? BannerKey,
    string? Bio,
    string? StatusMessage
);

// Returned for /api/users/me/blocks — the blocked user's public identity + when blocked
public record BlockResponse(
    long Id,
    string Username,
    string? Discriminator,
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