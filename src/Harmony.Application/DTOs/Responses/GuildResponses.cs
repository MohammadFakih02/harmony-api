namespace Harmony.Application.DTOs.Responses;

public record GuildResponse(
    long Id,
    string Name,
    string? Description,
    long OwnerId,
    string? IconKey,
    string? BannerKey,
    bool IsPublic,
    int MemberCount,
    long CreatedAt,
    long? WelcomeChannelId,
    string? WelcomeMessage,
    bool SystemMessagesEnabled,
    bool RequireVerifiedEmail
);

/// <summary>A soft-deleted guild as shown in the owner's global Trash (§5.71 #5) — identity + icon +
/// when it was deleted (drives the "auto-deletes in N days" hint client-side).</summary>
public record DeletedGuildResponse(long Id, string Name, string? IconKey, long? DeletedAt);

public record GuildMemberResponse(
    long UserId,
    string Username,
    string? Nickname,
    string? AvatarKey,
    bool IsOwner,
    long JoinedAt,
    long? CommunicationDisabledUntil,
    IEnumerable<long> RoleIds
);

/// <summary>The caller's guild-level capabilities (resolved bits → booleans), so the client can
/// show/hide management + moderation UI without reasoning about permission bits.</summary>
public record GuildCapabilitiesResponse(
    bool CanManageGuild,
    bool CanManageChannels,
    bool CanManageRoles,
    bool CanCreateInvite,
    bool CanManageInvites,
    bool CanKick,
    bool CanBan,
    bool CanTimeout,
    bool CanViewAuditLog,
    bool CanManageNicknames,
    bool CanMuteMembers,
    bool CanDeafenMembers,
    bool CanMoveMembers
);

public record GuildBanResponse(
    long UserId,
    string? Username,
    string? AvatarKey,
    long BannedBy,
    string? BannedByUsername,
    string? Reason,
    long CreatedAt
);