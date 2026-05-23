namespace Harmony.Core.DTOs.Responses;

public record AuthResponse(
    string AccessToken,
    UserResponse User
);

public record UserResponse(
    long Id,
    string Username,
    string? Discriminator,
    string Email,
    string? AvatarKey,
    string AccountStatus
);