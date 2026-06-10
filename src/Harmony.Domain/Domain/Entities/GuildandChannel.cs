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
    public string? InviteCode { get; set; }
    public int MemberCount { get; set; }
    public long CreatedAt { get; set; }

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
    public string Type { get; set; } = null!; // "text" | "voice" | "category" | "dm"
    public int Position { get; set; }
    public long? CategoryId { get; set; }
    public bool IsNsfw { get; set; }
    public int SlowmodeSeconds { get; set; }
    public int? Bitrate { get; set; }
    public int? UserLimit { get; set; }
    public long CreatedAt { get; set; }

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
