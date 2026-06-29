namespace Harmony.Application.DTOs.Requests;

public record CreateGuildRequest(
    string Name,
    string? Description
);

public record UpdateGuildRequest(
    string? Name,
    string? Description,
    bool? IsPublic
);

/// <summary>Timeout a member for <see cref="DurationSeconds"/> seconds from now (max 28 days).</summary>
public record TimeoutMemberRequest(long DurationSeconds);

/// <summary>Ban a member, with an optional moderator-supplied reason recorded in the audit log.</summary>
public record BanMemberRequest(string? Reason);

/// <summary>Set (or clear) a member's server nickname. Null/blank clears it back to the username.</summary>
public record SetNicknameRequest(string? Nickname);