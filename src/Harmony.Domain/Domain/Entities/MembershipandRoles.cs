namespace Harmony.Domain.Domain.Entities;

public class GuildMember
{
    public long UserId { get; set; }
    public long GuildId { get; set; }
    public string? Nickname { get; set; }
    public long JoinedAt { get; set; }
    public bool IsOwner { get; set; }
    public long? CommunicationDisabledUntil { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public Guild Guild { get; set; } = null!;
}

public class GuildBan
{
    public long GuildId { get; set; }
    public long UserId { get; set; }
    public long BannedBy { get; set; }
    public string? Reason { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public Guild Guild { get; set; } = null!;
    public User User { get; set; } = null!;
    public User BannedByUser { get; set; } = null!;
}

public class Role
{
    public long Id { get; set; }
    public long GuildId { get; set; }
    public string Name { get; set; } = null!;
    public int Color { get; set; }
    public long PermissionBits { get; set; }
    public int Position { get; set; }
    public bool IsHoisted { get; set; }
    public bool IsMentionable { get; set; }
    public bool IsDefault { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public Guild Guild { get; set; } = null!;
    public ICollection<RoleAssignment> Assignments { get; set; } = [];
    public ICollection<ChannelPermissionOverride> ChannelOverrides { get; set; } = [];
}

public class RoleAssignment
{
    public long UserId { get; set; }
    public long RoleId { get; set; }
    public long GuildId { get; set; }
    public long AssignedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

public class ChannelPermissionOverride
{
    public long Id { get; set; }
    public long ChannelId { get; set; }
    public long TargetId { get; set; }
    public string TargetType { get; set; } = null!;   // "role" | "user"
    public long AllowBits { get; set; }
    public long DenyBits { get; set; }

    // Navigation
    public Channel Channel { get; set; } = null!;
}