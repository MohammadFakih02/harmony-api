namespace Harmony.Application.DTOs.Responses;

/// <summary>A guild role. <c>PermissionBits</c> is a long serialized as a string (LongStringConverter);
/// clients coerce it back. Higher <c>Position</c> = higher rank; <c>@everyone</c> is position 0.</summary>
public record RoleResponse(
    long Id,
    long GuildId,
    string Name,
    int Color,
    long PermissionBits,
    int Position,
    bool IsHoisted,
    bool IsMentionable,
    bool IsDefault
);
