namespace Harmony.Core.DTOs.Requests;

public record UpdateUserRequest(
    string? Username,
    string? Bio,
    string? StatusMessage
);