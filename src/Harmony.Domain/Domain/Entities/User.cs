using Microsoft.AspNetCore.Identity;

namespace Harmony.Domain.Domain.Entities;

public class User : IdentityUser<long>
{
    // IdentityUser<long> provides: Id, UserName, Email, PasswordHash, etc.
    // We map UserName → username, Email → email in configuration.

    public string? AvatarKey { get; set; }
    public string? BannerKey { get; set; }
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
