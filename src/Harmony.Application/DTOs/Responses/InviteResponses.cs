namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// A managed guild invite, enriched with the creator's identity (batch-resolved on list).
/// <c>ExpiresAt</c>/<c>MaxUses</c>/<c>ChannelId</c> null mean never-expires / unlimited /
/// guild-level.
/// </summary>
public record InviteResponse(
    string Code,
    long GuildId,
    long? ChannelId,
    long CreatorId,
    string? CreatorUsername,
    int? MaxUses,
    int UseCount,
    long? ExpiresAt,
    long CreatedAt
);

/// <summary>
/// Public preview of an invite shown before joining — just enough to render the join card.
/// </summary>
public record InvitePreviewResponse(
    string Code,
    long GuildId,
    string GuildName,
    int MemberCount,
    long? ChannelId
);
