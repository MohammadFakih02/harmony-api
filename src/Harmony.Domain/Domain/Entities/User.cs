using Microsoft.AspNetCore.Identity;

namespace Harmony.Domain.Domain.Entities;

public class User : IdentityUser<long>
{
    // IdentityUser<long> provides: Id, UserName, Email, PasswordHash, etc.
    // We map UserName → username, Email → email in configuration.

    public string? AvatarKey { get; set; }
    public string? BannerKey { get; set; }

    /// <summary>User-picked profile banner colour ("#rrggbb"); shown when no banner image is set.
    /// Independent of theme/role colours. Null = default banner.</summary>
    public string? BannerColor { get; set; }
    public string? Bio { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>Date of birth (date only, no time). Drives the displayed age + future NSFW gating; null = unset.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>Unix-ms when the custom <see cref="StatusMessage"/> auto-clears; null = never.</summary>
    public long? StatusMessageExpiresAt { get; set; }
    public string PreferredStatus { get; set; } = "online"; // online | away | dnd | invisible

    /// <summary>Unix-ms when <see cref="PreferredStatus"/> auto-reverts to online; null = never.</summary>
    public long? PreferredStatusExpiresAt { get; set; }
    public string AccountStatus { get; set; } = "active";

    /// <summary>Who may open a new DM with this user: "everyone" | "friends_only". Default "everyone".</summary>
    public string DmPrivacy { get; set; } = "everyone";

    /// <summary>The user's personal guild-rail order (guild ids, first = top). Guilds not in the
    /// list (new joins) append after it in join order; null = pure join order.</summary>
    public long[]? GuildOrder { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<GuildMember> GuildMemberships { get; set; } = [];
    public ICollection<RoleAssignment> RoleAssignments { get; set; } = [];
    public ICollection<Friend> SentFriendRequests { get; set; } = [];
    public ICollection<Friend> ReceivedFriendRequests { get; set; } = [];
    public ICollection<UserBlock> Blocks { get; set; } = [];
    public ICollection<UserBlock> BlockedBy { get; set; } = [];
    public ICollection<UserMute> Mutes { get; set; } = [];
    public ICollection<Notification> Notifications { get; set; } = [];
    public NotificationPreference? NotificationPreference { get; set; }
    public ICollection<UserPushSubscription> PushSubscriptions { get; set; } = [];
    public VoiceState? VoiceState { get; set; }
}
