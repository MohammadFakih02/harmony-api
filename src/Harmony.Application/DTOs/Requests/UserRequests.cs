namespace Harmony.Application.DTOs.Requests;

public record UpdateUserRequest(
    string? Username,
    string? Bio,
    string? StatusMessage
);

// PATCH /api/users/me/status — durable preferred status (online|away|dnd|invisible)
public record UpdateStatusRequest(string Status);

// POST /api/mutes — mute a guild, channel, or user. MutedUntil is an absolute
// unix-ms timestamp; null mutes indefinitely (until manual unmute).
public record CreateMuteRequest(string TargetType, long TargetId, long? MutedUntil);