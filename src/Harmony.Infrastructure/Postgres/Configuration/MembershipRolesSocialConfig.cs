using Harmony.Domain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Harmony.Infrastructure.Postgres.Configuration;

public class GuildMemberConfiguration : IEntityTypeConfiguration<GuildMember>
{
    public void Configure(EntityTypeBuilder<GuildMember> builder)
    {
        builder.ToTable("GuildMembers");
        builder.HasKey(m => new { m.UserId, m.GuildId });
        builder.Property(m => m.UserId).HasColumnName("user_id");
        builder.Property(m => m.GuildId).HasColumnName("guild_id");
        builder.Property(m => m.Nickname).HasColumnName("nickname").HasMaxLength(32);
        builder.Property(m => m.JoinedAt).HasColumnName("joined_at");
        builder.Property(m => m.IsOwner).HasColumnName("is_owner");
        builder.Property(m => m.CommunicationDisabledUntil).HasColumnName("communication_disabled_until");

        builder.HasOne(m => m.User)
            .WithMany(u => u.GuildMemberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Guild)
            .WithMany(g => g.Members)
            .HasForeignKey(m => m.GuildId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class GuildBanConfiguration : IEntityTypeConfiguration<GuildBan>
{
    public void Configure(EntityTypeBuilder<GuildBan> builder)
    {
        builder.ToTable("GuildBans");
        builder.HasKey(b => new { b.GuildId, b.UserId });
        builder.Property(b => b.GuildId).HasColumnName("guild_id");
        builder.Property(b => b.UserId).HasColumnName("user_id");
        builder.Property(b => b.BannedBy).HasColumnName("banned_by");
        builder.Property(b => b.Reason).HasColumnName("reason").HasMaxLength(512);
        builder.Property(b => b.CreatedAt).HasColumnName("created_at");

        builder.HasOne(b => b.Guild)
            .WithMany()
            .HasForeignKey(b => b.GuildId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The moderator who issued the ban — kept on record; don't cascade-delete the ban row
        // if that moderator's account is later removed.
        builder.HasOne(b => b.BannedByUser)
            .WithMany()
            .HasForeignKey(b => b.BannedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.GuildId).HasColumnName("guild_id");
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(r => r.Color).HasColumnName("color");
        builder.Property(r => r.PermissionBits).HasColumnName("permission_bits");
        builder.Property(r => r.Position).HasColumnName("position");
        builder.Property(r => r.IsHoisted).HasColumnName("is_hoisted");
        builder.Property(r => r.IsMentionable).HasColumnName("is_mentionable");
        builder.Property(r => r.IsDefault).HasColumnName("is_default");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        builder.HasOne(r => r.Guild)
            .WithMany(g => g.Roles)
            .HasForeignKey(r => r.GuildId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.GuildId);
    }
}

public class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("RoleAssignments");
        builder.HasKey(ra => new { ra.UserId, ra.RoleId });
        builder.Property(ra => ra.UserId).HasColumnName("user_id");
        builder.Property(ra => ra.RoleId).HasColumnName("role_id");
        builder.Property(ra => ra.GuildId).HasColumnName("guild_id");
        builder.Property(ra => ra.AssignedAt).HasColumnName("assigned_at");

        builder.HasOne(ra => ra.User)
            .WithMany(u => u.RoleAssignments)
            .HasForeignKey(ra => ra.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ra => ra.Role)
            .WithMany(r => r.Assignments)
            .HasForeignKey(ra => ra.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ra => ra.GuildId);
    }
}

public class ChannelPermissionOverrideConfiguration : IEntityTypeConfiguration<ChannelPermissionOverride>
{
    public void Configure(EntityTypeBuilder<ChannelPermissionOverride> builder)
    {
        builder.ToTable("ChannelPermissionOverrides");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(o => o.ChannelId).HasColumnName("channel_id");
        builder.Property(o => o.TargetId).HasColumnName("target_id");
        builder.Property(o => o.TargetType).HasColumnName("target_type").HasMaxLength(8).IsRequired();
        builder.Property(o => o.AllowBits).HasColumnName("allow_bits");
        builder.Property(o => o.DenyBits).HasColumnName("deny_bits");

        builder.HasOne(o => o.Channel)
            .WithMany(c => c.PermissionOverrides)
            .HasForeignKey(o => o.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(o => new { o.ChannelId, o.TargetId, o.TargetType }).IsUnique();
    }
}

public class FriendConfiguration : IEntityTypeConfiguration<Friend>
{
    public void Configure(EntityTypeBuilder<Friend> builder)
    {
        builder.ToTable("Friends");
        builder.HasKey(f => new { f.RequesterId, f.AddresseeId });
        builder.Property(f => f.RequesterId).HasColumnName("requester_id");
        builder.Property(f => f.AddresseeId).HasColumnName("addressee_id");
        builder.Property(f => f.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(f => f.CreatedAt).HasColumnName("created_at");
        builder.Property(f => f.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(f => f.Requester)
            .WithMany(u => u.SentFriendRequests)
            .HasForeignKey(f => f.RequesterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.Addressee)
            .WithMany(u => u.ReceivedFriendRequests)
            .HasForeignKey(f => f.AddresseeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> builder)
    {
        builder.ToTable("UserBlocks");
        builder.HasKey(b => new { b.BlockerId, b.BlockedId });
        builder.Property(b => b.BlockerId).HasColumnName("blocker_id");
        builder.Property(b => b.BlockedId).HasColumnName("blocked_id");
        builder.Property(b => b.CreatedAt).HasColumnName("created_at");

        builder.HasOne(b => b.Blocker)
            .WithMany(u => u.Blocks)
            .HasForeignKey(b => b.BlockerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(b => b.Blocked)
            .WithMany(u => u.BlockedBy)
            .HasForeignKey(b => b.BlockedId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class UserMuteConfiguration : IEntityTypeConfiguration<UserMute>
{
    public void Configure(EntityTypeBuilder<UserMute> builder)
    {
        builder.ToTable("UserMutes");
        builder.HasKey(m => new { m.UserId, m.TargetId, m.TargetType });
        builder.Property(m => m.UserId).HasColumnName("user_id");
        builder.Property(m => m.TargetId).HasColumnName("target_id");
        builder.Property(m => m.TargetType).HasColumnName("target_type").HasMaxLength(16).IsRequired();
        builder.Property(m => m.MutedUntil).HasColumnName("muted_until");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");

        builder.HasOne(m => m.User)
            .WithMany(u => u.Mutes)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class UserNicknameConfiguration : IEntityTypeConfiguration<UserNickname>
{
    public void Configure(EntityTypeBuilder<UserNickname> builder)
    {
        builder.ToTable("UserNicknames");
        // PK leads with OwnerId, so the bulk "all my nicknames" read is index-covered.
        builder.HasKey(n => new { n.OwnerId, n.TargetId });
        builder.Property(n => n.OwnerId).HasColumnName("owner_id");
        builder.Property(n => n.TargetId).HasColumnName("target_id");
        builder.Property(n => n.Nickname).HasColumnName("nickname").HasMaxLength(32).IsRequired();
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");
        builder.Property(n => n.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(n => n.Owner)
            .WithMany()
            .HasForeignKey(n => n.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(n => n.Target)
            .WithMany()
            .HasForeignKey(n => n.TargetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class DirectMessageChannelConfiguration : IEntityTypeConfiguration<DirectMessageChannel>
{
    public void Configure(EntityTypeBuilder<DirectMessageChannel> builder)
    {
        builder.ToTable("DirectMessageChannels");
        builder.HasKey(d => new { d.ChannelId, d.UserId });
        builder.Property(d => d.ChannelId).HasColumnName("channel_id");
        builder.Property(d => d.UserId).HasColumnName("user_id");
        builder.Property(d => d.IsHidden).HasColumnName("is_hidden");
        builder.Property(d => d.LastReadId).HasColumnName("last_read_id");

        builder.HasOne(d => d.Channel)
            .WithMany(c => c.DirectMessageChannels)
            .HasForeignKey(d => d.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
