namespace Harmony.Application.DTOs.Requests;

// POST /api/dm — open (or reuse) a 1:1 DM channel with another user.
public record CreateDirectMessageRequest(long TargetUserId);

// POST /api/dm/group — create a group DM with two or more other users.
// Name is optional (empty → the client renders the joined participant names).
public record CreateGroupDmRequest(string? Name, IReadOnlyList<long> UserIds);

// POST /api/dm/{channelId}/participants — add a user to a group DM.
public record AddGroupParticipantRequest(long UserId);

// PATCH /api/dm/{channelId}/name — rename a group DM (empty clears back to the joined names).
public record RenameGroupDmRequest(string? Name);
