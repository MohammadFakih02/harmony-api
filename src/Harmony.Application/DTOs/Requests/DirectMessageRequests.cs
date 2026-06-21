namespace Harmony.Application.DTOs.Requests;

// POST /api/dm — open (or reuse) a 1:1 DM channel with another user.
public record CreateDirectMessageRequest(long TargetUserId);
