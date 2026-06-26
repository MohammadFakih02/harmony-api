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
    long JoinedAt
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