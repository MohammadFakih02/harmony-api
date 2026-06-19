namespace Harmony.Application.DTOs.Requests;

public record UpdateUserRequest(
    string? Username,
    string? Bio,
    string? StatusMessage
);

// PATCH /api/users/me/status — durable preferred status (online|away|dnd|invisible)
public record UpdateStatusRequest(string Status);