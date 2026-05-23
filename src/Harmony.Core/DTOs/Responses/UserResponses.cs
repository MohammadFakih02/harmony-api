namespace Harmony.Core.DTOs.Responses;

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