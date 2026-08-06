namespace Harmony.Domain.Domain.Entities;

public class Friend
{
    public long RequesterId { get; set; }
    public long AddresseeId { get; set; }
    public string Status { get; set; } = null!;    // "pending" | "accepted" | "declined"
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    // Navigation
    public User Requester { get; set; } = null!;
    public User Addressee { get; set; } = null!;
}

public class UserBlock
{
    public long BlockerId { get; set; }
    public long BlockedId { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public User Blocker { get; set; } = null!;
    public User Blocked { get; set; } = null!;
}

public class UserMute
{
    public long UserId { get; set; }
    public long TargetId { get; set; }
    public string TargetType { get; set; } = null!;   // "user" | "guild" | "channel"
    public long? MutedUntil { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}

/// <summary>
/// A private, one-directional alias the <see cref="OwnerId"/> user has set for another user
/// (<see cref="TargetId"/>). Visible only to the owner — used as the friend/DM display name.
/// Independent of guild membership (you can nickname anyone you DM, not just friends).
/// </summary>
public class UserNickname
{
    public long OwnerId { get; set; }
    public long TargetId { get; set; }
    public string Nickname { get; set; } = null!;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    // Navigation
    public User Owner { get; set; } = null!;
    public User Target { get; set; } = null!;
}

public class DirectMessageChannel
{
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public bool IsHidden { get; set; }
    public long LastReadId { get; set; }

    // Navigation
    public Channel Channel { get; set; } = null!;
    public User User { get; set; } = null!;
}