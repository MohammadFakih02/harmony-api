namespace Harmony.Application.DTOs.Requests;

public record CreateGuildRequest(
    string Name,
    string? Description
);

public record UpdateGuildRequest(
    string? Name,
    string? Description,
    bool? IsPublic,
    bool? RequireVerifiedEmail
);

/// <summary>
/// Replace a guild's welcome configuration (ManageGuild). <see cref="WelcomeChannelId"/> null = post
/// joins to the default text channel; <see cref="WelcomeMessage"/> null/blank = built-in greeting;
/// <see cref="SystemMessagesEnabled"/> false = suppress member-join notices entirely.
/// </summary>
public record UpdateGuildWelcomeRequest(
    long? WelcomeChannelId,
    string? WelcomeMessage,
    bool SystemMessagesEnabled
);

/// <summary>Timeout a member for <see cref="DurationSeconds"/> seconds from now (max 28 days).</summary>
public record TimeoutMemberRequest(long DurationSeconds);

/// <summary>Ban a member, with an optional moderator-supplied reason recorded in the audit log.</summary>
public record BanMemberRequest(string? Reason);

/// <summary>Set (or clear) a member's server nickname. Null/blank clears it back to the username.</summary>
public record SetNicknameRequest(string? Nickname);