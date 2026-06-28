namespace Harmony.Application.DTOs.Requests;

public record UpdateUserRequest(
    string? Username,
    string? Bio,
    string? StatusMessage,
    // ISO date "yyyy-MM-dd". Null = leave unchanged; the empty string clears it.
    string? DateOfBirth
);

// PATCH /api/users/me/status — durable preferred status (online|away|dnd|invisible).
// ExpiresInMinutes (optional) auto-reverts the status to online after that many minutes;
// null/absent = no expiry. Ignored for "online" (it's already the default).
public record UpdateStatusRequest(string Status, int? ExpiresInMinutes);

// PATCH /api/users/me/custom-status — the free-text custom status message + its clear-after.
// A null/empty Message clears it. ExpiresInMinutes (optional) auto-clears it later.
public record UpdateCustomStatusRequest(string? Message, int? ExpiresInMinutes);

// POST /api/mutes — mute a guild, channel, or user. MutedUntil is an absolute
// unix-ms timestamp; null mutes indefinitely (until manual unmute).
public record CreateMuteRequest(string TargetType, long TargetId, long? MutedUntil);