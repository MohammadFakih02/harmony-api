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
    string PreferredStatus,
    string AccountStatus,
    long CreatedAt
);

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