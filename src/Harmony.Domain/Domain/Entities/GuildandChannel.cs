namespace Harmony.Domain.Domain.Entities;

public class Guild
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public long OwnerId { get; set; }
    public string? IconKey { get; set; }
    public string? BannerKey { get; set; }
    public bool IsPublic { get; set; }
    public int MemberCount { get; set; }
    public long CreatedAt { get; set; }

    // Welcome / system messages (§5.31, roadmap E#16). welcome_channel_id null = post to the
    // default text channel; welcome_message null = use a built-in default greeting; system
    // messages (e.g. member-join notices) are suppressed entirely when SystemMessagesEnabled is false.
    public long? WelcomeChannelId { get; set; }
    public string? WelcomeMessage { get; set; }
    public bool SystemMessagesEnabled { get; set; } = true;

    /// <summary>When true, joining this guild (invite redeem or discovery join) requires a verified
    /// email address. Default false — opt-in per guild.</summary>
    public bool RequireVerifiedEmail { get; set; }

    /// <summary>Soft-delete tombstone (unix ms) — null = live. A deleted guild is excluded from every
    /// normal read (rail, load, discovery) but recoverable from the owner's Trash until the 30-day
    /// auto-purge (or a permanent delete) hard-removes it. §5.71 (#5).</summary>
    public long? DeletedAt { get; set; }

    // Navigation
    public User Owner { get; set; } = null!;
    public ICollection<Channel> Channels { get; set; } = [];
    public ICollection<GuildMember> Members { get; set; } = [];
    public ICollection<Role> Roles { get; set; } = [];
    public ICollection<GuildInvite> Invites { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
    public ICollection<VoiceState> VoiceStates { get; set; } = [];
}

public class Channel
{
    public long Id { get; set; }
    public long? GuildId { get; set; }
    public string Name { get; set; } = null!;
    public string? Topic { get; set; }
    public string Type { get; set; } = null!; // "text" | "voice" | "category" | "dm" | "group_dm"
    public int Position { get; set; }

    // Group-DM icon (storage key under channel-icons/{channelId}/...). Null for every other
    // channel type — guild channels have no per-channel icon.
    public string? IconKey { get; set; }
    public long? CategoryId { get; set; }
    public bool IsNsfw { get; set; }
    public int SlowmodeSeconds { get; set; }
    public int? Bitrate { get; set; }
    public int? UserLimit { get; set; }
    public long CreatedAt { get; set; }

    /// <summary>Soft-delete tombstone (unix ms) — null = live. Only guild channels are ever soft-deleted
    /// (DMs/categories are unaffected); a deleted channel is hidden from the sidebar but its messages
    /// are preserved in Scylla until restore, a permanent delete, or the 30-day auto-purge. §5.71 (#5).</summary>
    public long? DeletedAt { get; set; }

    // Navigation
    public Guild? Guild { get; set; }
    public Channel? Category { get; set; }
    public ICollection<Channel> Children { get; set; } = [];
    public ICollection<ChannelPermissionOverride> PermissionOverrides { get; set; } = [];
    public ICollection<DirectMessageChannel> DirectMessageChannels { get; set; } = [];
    public ICollection<FileAttachment> FileAttachments { get; set; } = [];
    public ICollection<GuildInvite> Invites { get; set; } = [];
    public ICollection<VoiceState> VoiceStates { get; set; } = [];
    // MessageSearchEntries intentionally removed — FK was dropped in DecoupleSearchIndex migration.
    // MessagesSearch is now a standalone read model with no relational constraint to Channels.
}
