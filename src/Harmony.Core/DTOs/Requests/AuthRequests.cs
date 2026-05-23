namespace Harmony.Core.DTOs.Requests;

public record RegisterRequest(string Username, string Email, string Password);

public record LoginRequest(string Email, string Password);

public record RefreshRequest; // body is empty — refresh token comes from httpOnly cookie
