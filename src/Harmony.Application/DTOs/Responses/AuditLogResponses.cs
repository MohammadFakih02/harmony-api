namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// One audit-log entry, enriched with the actor's public identity (batch-resolved on read,
/// no N+1). <c>Changes</c> is the raw jsonb string as stored — the client parses it per
/// <c>ActionType</c>. <c>ActorUsername</c>/<c>ActorAvatarKey</c> are null if the actor account
/// no longer exists.
/// </summary>
public record AuditLogEntryResponse(
    long Id,
    long ActorId,
    string? ActorUsername,
    string? ActorAvatarKey,
    string ActionType,
    long? TargetId,
    string? Changes,
    string? Reason,
    long CreatedAt
);
