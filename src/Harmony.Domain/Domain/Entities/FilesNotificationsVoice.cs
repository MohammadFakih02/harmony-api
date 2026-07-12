namespace Harmony.Domain.Domain.Entities;

public class FileAttachment
{
    public long Id { get; set; }
    public long UploaderId { get; set; }
    public long? GuildId { get; set; } // null for DM attachments — a DM has no guild
    public long? ChannelId { get; set; } // null for user assets (avatar/banner) — no channel container
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
    public Channel? Channel { get; set; }
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

/// <summary>
/// A user's per-guild or per-channel notification level (§5.31, roadmap E#16). Distinct from the
/// global <see cref="NotificationPreference"/> (which is the master switch) and from
/// <c>UserMutes</c> (temporary silencing). Resolution for a notification is channel-scope →
/// guild-scope → default "mentions". A missing row everywhere means the default applies.
/// </summary>
public class NotificationSetting
{
    public long UserId { get; set; }
    public string ScopeType { get; set; } = null!; // "guild" | "channel"
    public long ScopeId { get; set; } // guildId or channelId, per ScopeType
    public string Level { get; set; } = null!; // "all" | "mentions" | "nothing"

    // When true, an @everyone/@here-only mention in this scope does not notify the user
    // (a direct @user or @role mention still does). Resolved channel-scope-over-guild-scope,
    // same as Level. Default false = the broadcast pings as normal.
    public bool SuppressEveryone { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}

/// <summary>
/// A pending web-push intent (transactional outbox). Producers add a row in the same
/// SaveChanges as the Notification row it mirrors (atomic), and PushNotificationService
/// dispatches due rows — so a crash between "row committed" and "push sent" is recovered
/// on restart (at-least-once; duplicate pushes collapse client-side via the notification tag).
/// Rows are deleted on successful dispatch or after MaxAttempts transient failures.
/// </summary>
public class PushOutboxMessage
{
    public long Id { get; set; }
    public string Kind { get; set; } = null!; // "mention" | "reply" | "friend_request" | "dm"
    public long RecipientId { get; set; } // 0 for "dm" — fan-out resolved at dispatch time
    public long? ActorId { get; set; }
    public long? GuildId { get; set; }
    public long? ChannelId { get; set; }
    public long? MessageId { get; set; }
    public string? ExcludeUserIds { get; set; } // comma-joined snowflakes; "dm" only
    public int Attempts { get; set; }
    public long NextAttemptAt { get; set; } // unix-ms; due when <= now
    public long CreatedAt { get; set; }
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
    public long? ChannelId { get; set; } // null = guild-level invite (no specific landing channel)
    public long CreatorId { get; set; }
    public int? MaxUses { get; set; }
    public int UseCount { get; set; }
    public long? ExpiresAt { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public Guild Guild { get; set; } = null!;
    public Channel? Channel { get; set; }
    public User Creator { get; set; } = null!;
}

public class VoiceState
{
    public long UserId { get; set; }
    public long? GuildId { get; set; } // null for a DM / group-DM call — those are guild-less
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
    public Guild? Guild { get; set; }
    public Channel Channel { get; set; } = null!;
}

public class AuditLog
{
    public long Id { get; set; }
    public long GuildId { get; set; }
    public long ActorId { get; set; }
    public string ActionType { get; set; } = null!;
    public long? TargetId { get; set; }
    public string? Changes { get; set; } // JSONB stored as string; deserialize when needed
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
}
