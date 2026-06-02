using Harmony.Domain.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres;

// IdentityDbContext<User, Role, long> would conflict with our own Role entity,
// so we inherit the base IdentityDbContext and supply all five type params explicitly.
// Our Role entity is Harmony.Core.Domain.Entities.Role (guild roles, not Identity roles).
// Identity's role type is IdentityRole<long> — stored in a separate AspNetRoles table.
public class HarmonyDbContext
    : IdentityDbContext<
        User,
        IdentityRole<long>,
        long,
        IdentityUserClaim<long>,
        IdentityUserRole<long>,
        IdentityUserLogin<long>,
        IdentityRoleClaim<long>,
        IdentityUserToken<long>
    >
{
    public HarmonyDbContext(DbContextOptions<HarmonyDbContext> options)
        : base(options) { }

    // Harmony domain tables
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<GuildMember> GuildMembers => Set<GuildMember>();
    public DbSet<Role> GuildRoles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<ChannelPermissionOverride> ChannelPermissionOverrides =>
        Set<ChannelPermissionOverride>();
    public DbSet<Friend> Friends => Set<Friend>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<UserMute> UserMutes => Set<UserMute>();
    public DbSet<DirectMessageChannel> DirectMessageChannels => Set<DirectMessageChannel>();
    public DbSet<FileAttachment> FileAttachments => Set<FileAttachment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<UserPushSubscription> UserPushSubscriptions => Set<UserPushSubscription>();
    public DbSet<GuildInvite> GuildInvites => Set<GuildInvite>();
    public DbSet<VoiceState> VoiceStates => Set<VoiceState>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<MessageSearch> MessagesSearch => Set<MessageSearch>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder); // must call — sets up Identity tables

        // Apply all IEntityTypeConfiguration<T> classes found in this assembly
        builder.ApplyConfigurationsFromAssembly(typeof(HarmonyDbContext).Assembly);
    }
}
