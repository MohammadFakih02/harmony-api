namespace Harmony.Application.DTOs.Requests;

public record RegisterRequest(string Username, string Email, string Password);

// Identifier is the user's email OR username — login resolves either.
public record LoginRequest(string Identifier, string Password);

public record RefreshRequest; // body is empty — refresh token comes from httpOnly cookie

// UserId travels as a string (Snowflake precision over JSON/JS) even though it's parsed as a long
// server-side — same wire rule as every other emailed-link/auth DTO.
public record ConfirmEmailRequest(string UserId, string Token);

// --- Email-code 2FA (Stage B) ---

public record Verify2faRequest(string ChallengeToken, string Code, bool RememberDevice);

public record Resend2faRequest(string ChallengeToken);

public record Enable2faRequest(string Password);

public record Confirm2faRequest(string Code);

public record Disable2faRequest(string Password);

// --- Forgot password (Stage C) ---

public record ForgotPasswordRequest(string Email);

// UserId travels as a string (Snowflake precision), same wire rule as ConfirmEmailRequest.
public record ResetPasswordRequest(string UserId, string Token, string NewPassword);

// --- Google sign-in (Stage D) ---

// Username is null on the FIRST call. If the ID token resolves to no existing account, the response
// comes back with NeedsUsername and the caller re-posts the SAME token plus a chosen username, which
// is when the account is actually created. Sign-ins and auto-links ignore Username entirely — an
// existing account's name is never overwritten by this path.
public record GoogleLoginRequest(string IdToken, string? Username = null);

// --- Credential changes (Stage E) ---

// Code is the emailed 2FA step-up code (D20) — null on the first attempt for a 2FA-enabled
// account (the server replies RequiresCode and emails one), populated on the follow-up call.
// Always null/ignored for a non-2FA account.
public record ChangePasswordRequest(string CurrentPassword, string NewPassword, string? Code = null);

public record SetPasswordRequest(string NewPassword);

public record ChangeEmailRequest(string Password, string NewEmail, string? Code = null);

// UserId travels as a string (Snowflake precision), same wire rule as ConfirmEmailRequest.
public record ConfirmEmailChangeRequest(string UserId, string Email, string Token);

public record ChangeUsernameRequest(string Password, string NewUsername);
