namespace Harmony.Application.DTOs.Requests;

public record RegisterRequest(string Username, string Email, string Password);

// Identifier is the user's email OR username — login resolves either.
public record LoginRequest(string Identifier, string Password);

public record RefreshRequest; // body is empty — refresh token comes from httpOnly cookie
