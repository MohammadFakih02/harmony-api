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
    long CreatedAt
);

public record GuildMemberResponse(
    long UserId,
    string Username,
    string? Nickname,
    string? AvatarKey,
    bool IsOwner,
    long JoinedAt,
    long? CommunicationDisabledUntil
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
    bool CanViewAuditLog
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