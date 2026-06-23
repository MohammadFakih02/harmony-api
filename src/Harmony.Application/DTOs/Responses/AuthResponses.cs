namespace Harmony.Application.DTOs.Responses;

public record AuthResponse(
    string AccessToken,
    UserResponse User
);

public record UserResponse(
    long Id,
    string Username,
    string Email,
    string? AvatarKey,
    string AccountStatus
);