namespace Harmony.Core.Domain.Entities;

public class FileAttachment
{
    public long Id { get; set; }
    public long UploaderId { get; set; }
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public string MinioKey { get; set; } = null!;
    public string Filename { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool IsConfirmed { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public User Uploader { get; set; } = null!;
    public Channel Channel { get; set; } = null!;
}

public class Notification
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Type { get; set; } = null!;
    public long ActorId { get; set; }
    public long? GuildId { get; set; }
    public long? ChannelId { get; set; }
    public long? MessageId { get; set; }
    public bool IsRead { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public User Actor { get; set; } = null!;
}

public class NotificationPreference
{
    public long UserId { get; set; }
    public bool MentionsEnabled { get; set; } = true;
    public bool RepliesEnabled { get; set; } = true;
    public bool FriendRequests { get; set; } = true;
    public bool GuildInvites { get; set; } = true;
    public bool PushEnabled { get; set; } = true;

    // Navigation
    public User User { get; set; } = null!;
}

public class UserPushSubscription
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Endpoint { get; set; } = null!;
    public string P256dh { get; set; } = null!;
    public string AuthKey { get; set; } = null!;
    public long CreatedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}

public class GuildInvite
{
    public string Code { get; set; } = null!;
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public long CreatorId { get; set; }
    public int? MaxUses { get; set; }
    public int UseCount { get; set; }
    public long? ExpiresAt { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public Guild Guild { get; set; } = null!;
    public Channel Channel { get; set; } = null!;
    public User Creator { get; set; } = null!;
}

public class VoiceState
{
    public long UserId { get; set; }
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public bool IsMuted { get; set; }
    public bool IsDeafened { get; set; }
    public bool IsServerMuted { get; set; }
    public bool IsServerDeafened { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsVideoOn { get; set; }
    public long JoinedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public Guild Guild { get; set; } = null!;
    public Channel Channel { get; set; } = null!;
}

public class AuditLog
{
    public long Id { get; set; }
    public long GuildId { get; set; }
    public long ActorId { get; set; }
    public string ActionType { get; set; } = null!;
    public long? TargetId { get; set; }
    public string? Changes { get; set; }             // JSONB stored as string; deserialize when needed
    public string? Reason { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public Guild Guild { get; set; } = null!;
    public User Actor { get; set; } = null!;
}

public class MessageSearch
{
    public long MessageId { get; set; }
    public long ChannelId { get; set; }
    public long? GuildId { get; set; }
    public long UserId { get; set; }
    public string Content { get; set; } = null!;
    // content_search tsvector is managed by PostgreSQL trigger — not mapped as a CLR property.
    public long CreatedAt { get; set; }

    // Navigation
    public Channel Channel { get; set; } = null!;
}