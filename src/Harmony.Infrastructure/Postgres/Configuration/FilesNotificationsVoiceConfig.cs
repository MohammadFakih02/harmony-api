using Harmony.Domain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Harmony.Infrastructure.Postgres.Configuration;

public class FileAttachmentConfiguration : IEntityTypeConfiguration<FileAttachment>
{
    public void Configure(EntityTypeBuilder<FileAttachment> builder)
    {
        builder.ToTable("FileAttachments");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(f => f.UploaderId).HasColumnName("uploader_id");
        builder.Property(f => f.GuildId).HasColumnName("guild_id");
        builder.Property(f => f.ChannelId).HasColumnName("channel_id");
        builder.Property(f => f.MinioKey).HasColumnName("minio_key").IsRequired();
        builder.Property(f => f.Filename).HasColumnName("filename").IsRequired();
        builder
            .Property(f => f.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(f => f.SizeBytes).HasColumnName("size_bytes");
        builder.Property(f => f.Width).HasColumnName("width");
        builder.Property(f => f.Height).HasColumnName("height");
        builder.Property(f => f.IsConfirmed).HasColumnName("is_confirmed");
        builder.Property(f => f.ThumbnailKey).HasColumnName("thumbnail_key");
        builder.Property(f => f.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(f => f.Uploader)
            .WithMany()
            .HasForeignKey(f => f.UploaderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne(f => f.Channel)
            .WithMany(c => c.FileAttachments)
            .HasForeignKey(f => f.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.ChannelId);
        builder.HasIndex(f => f.UploaderId);
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(n => n.UserId).HasColumnName("user_id");
        builder.Property(n => n.Type).HasColumnName("type").HasMaxLength(32).IsRequired();
        builder.Property(n => n.ActorId).HasColumnName("actor_id");
        builder.Property(n => n.GuildId).HasColumnName("guild_id");
        builder.Property(n => n.ChannelId).HasColumnName("channel_id");
        builder.Property(n => n.MessageId).HasColumnName("message_id");
        builder.Property(n => n.IsRead).HasColumnName("is_read");
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(n => n.User)
            .WithMany(u => u.Notifications)
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(n => n.Actor)
            .WithMany()
            .HasForeignKey(n => n.ActorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(n => new { n.UserId, n.IsRead });
        builder.HasIndex(n => n.CreatedAt);
    }
}

public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("NotificationPreferences");
        builder.HasKey(p => p.UserId);
        builder.Property(p => p.UserId).HasColumnName("user_id");
        builder
            .Property(p => p.MentionsEnabled)
            .HasColumnName("mentions_enabled")
            .HasDefaultValue(true);
        builder
            .Property(p => p.RepliesEnabled)
            .HasColumnName("replies_enabled")
            .HasDefaultValue(true);
        builder
            .Property(p => p.FriendRequests)
            .HasColumnName("friend_requests")
            .HasDefaultValue(true);
        builder.Property(p => p.GuildInvites).HasColumnName("guild_invites").HasDefaultValue(true);
        builder.Property(p => p.PushEnabled).HasColumnName("push_enabled").HasDefaultValue(true);

        builder
            .HasOne(p => p.User)
            .WithOne(u => u.NotificationPreference)
            .HasForeignKey<NotificationPreference>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class NotificationSettingConfiguration : IEntityTypeConfiguration<NotificationSetting>
{
    public void Configure(EntityTypeBuilder<NotificationSetting> builder)
    {
        builder.ToTable("NotificationSettings");
        // Composite PK: one level per (user, scope) — re-setting upserts the same row.
        builder.HasKey(s => new
        {
            s.UserId,
            s.ScopeType,
            s.ScopeId,
        });
        builder.Property(s => s.UserId).HasColumnName("user_id");
        builder.Property(s => s.ScopeType).HasColumnName("scope_type").HasMaxLength(16).IsRequired();
        builder.Property(s => s.ScopeId).HasColumnName("scope_id");
        builder.Property(s => s.Level).HasColumnName("level").HasMaxLength(16).IsRequired();
        builder
            .Property(s => s.SuppressEveryone)
            .HasColumnName("suppress_everyone")
            .HasDefaultValue(false);

        builder
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PushOutboxMessageConfiguration : IEntityTypeConfiguration<PushOutboxMessage>
{
    public void Configure(EntityTypeBuilder<PushOutboxMessage> builder)
    {
        builder.ToTable("PushOutbox");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
        builder.Property(p => p.RecipientId).HasColumnName("recipient_id");
        builder.Property(p => p.ActorId).HasColumnName("actor_id");
        builder.Property(p => p.GuildId).HasColumnName("guild_id");
        builder.Property(p => p.ChannelId).HasColumnName("channel_id");
        builder.Property(p => p.MessageId).HasColumnName("message_id");
        builder.Property(p => p.ExcludeUserIds).HasColumnName("exclude_user_ids");
        builder.Property(p => p.Attempts).HasColumnName("attempts");
        builder.Property(p => p.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");

        // The dispatcher's due-row poll filters on this every cycle.
        builder.HasIndex(p => p.NextAttemptAt);
    }
}

public class UserPushSubscriptionConfiguration : IEntityTypeConfiguration<UserPushSubscription>
{
    public void Configure(EntityTypeBuilder<UserPushSubscription> builder)
    {
        builder.ToTable("UserPushSubscriptions");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.UserId).HasColumnName("user_id");
        builder.Property(p => p.Endpoint).HasColumnName("endpoint").IsRequired();
        builder.Property(p => p.P256dh).HasColumnName("p256dh").IsRequired();
        builder.Property(p => p.AuthKey).HasColumnName("auth_key").IsRequired();
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(p => p.User)
            .WithMany(u => u.PushSubscriptions)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.UserId);
    }
}

public class GuildInviteConfiguration : IEntityTypeConfiguration<GuildInvite>
{
    public void Configure(EntityTypeBuilder<GuildInvite> builder)
    {
        builder.ToTable("GuildInvites");
        builder.HasKey(i => i.Code);
        builder.Property(i => i.Code).HasColumnName("code").HasMaxLength(16);
        builder.Property(i => i.GuildId).HasColumnName("guild_id");
        builder.Property(i => i.ChannelId).HasColumnName("channel_id");
        builder.Property(i => i.CreatorId).HasColumnName("creator_id");
        builder.Property(i => i.MaxUses).HasColumnName("max_uses");
        builder.Property(i => i.UseCount).HasColumnName("use_count");
        builder.Property(i => i.ExpiresAt).HasColumnName("expires_at");
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(i => i.Guild)
            .WithMany(g => g.Invites)
            .HasForeignKey(i => i.GuildId)
            .OnDelete(DeleteBehavior.Cascade);

        // Channel is optional (null = guild-level invite). Deleting the landing channel nulls
        // the invite's target rather than blocking the channel delete.
        builder
            .HasOne(i => i.Channel)
            .WithMany(c => c.Invites)
            .HasForeignKey(i => i.ChannelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder
            .HasOne(i => i.Creator)
            .WithMany()
            .HasForeignKey(i => i.CreatorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.GuildId);
    }
}

public class VoiceStateConfiguration : IEntityTypeConfiguration<VoiceState>
{
    public void Configure(EntityTypeBuilder<VoiceState> builder)
    {
        builder.ToTable("VoiceStates");
        builder.HasKey(v => v.UserId);
        builder.Property(v => v.UserId).HasColumnName("user_id");
        builder.Property(v => v.GuildId).HasColumnName("guild_id");
        builder.Property(v => v.ChannelId).HasColumnName("channel_id");
        builder.Property(v => v.IsMuted).HasColumnName("is_muted");
        builder.Property(v => v.IsDeafened).HasColumnName("is_deafened");
        builder.Property(v => v.IsServerMuted).HasColumnName("is_server_muted");
        builder.Property(v => v.IsServerDeafened).HasColumnName("is_server_deafened");
        builder.Property(v => v.IsStreaming).HasColumnName("is_streaming");
        builder.Property(v => v.IsVideoOn).HasColumnName("is_video_on");
        builder.Property(v => v.JoinedAt).HasColumnName("joined_at");

        builder
            .HasOne(v => v.User)
            .WithOne(u => u.VoiceState)
            .HasForeignKey<VoiceState>(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(v => v.Guild)
            .WithMany(g => g.VoiceStates)
            .HasForeignKey(v => v.GuildId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(v => v.Channel)
            .WithMany(c => c.VoiceStates)
            .HasForeignKey(v => v.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(v => v.GuildId);
        builder.HasIndex(v => v.ChannelId);
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.GuildId).HasColumnName("guild_id");
        builder.Property(a => a.ActorId).HasColumnName("actor_id");
        builder
            .Property(a => a.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(a => a.TargetId).HasColumnName("target_id");
        builder.Property(a => a.Changes).HasColumnName("changes").HasColumnType("jsonb");
        builder.Property(a => a.Reason).HasColumnName("reason");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(a => a.Guild)
            .WithMany(g => g.AuditLogs)
            .HasForeignKey(a => a.GuildId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(a => a.Actor)
            .WithMany()
            .HasForeignKey(a => a.ActorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.GuildId, a.CreatedAt });
    }
}

public class MessageSearchConfiguration : IEntityTypeConfiguration<MessageSearch>
{
    public void Configure(EntityTypeBuilder<MessageSearch> builder)
    {
        builder.ToTable("MessagesSearch");
        builder.HasKey(m => m.MessageId);
        builder.Property(m => m.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(m => m.ChannelId).HasColumnName("channel_id");
        builder.Property(m => m.GuildId).HasColumnName("guild_id");
        builder.Property(m => m.UserId).HasColumnName("user_id");
        builder.Property(m => m.Content).HasColumnName("content").IsRequired();
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(m => new { m.ChannelId, m.CreatedAt });
    }
}

public class MessageReactionConfiguration : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> builder)
    {
        builder.ToTable("MessageReactions");
        // Composite PK: one row per (message, emoji, user) — a re-reaction upserts the same row.
        builder.HasKey(r => new
        {
            r.MessageId,
            r.Emoji,
            r.UserId,
        });
        builder.Property(r => r.MessageId).HasColumnName("message_id");
        builder.Property(r => r.ChannelId).HasColumnName("channel_id");
        builder.Property(r => r.Emoji).HasColumnName("emoji").HasMaxLength(64).IsRequired();
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        // message_id/channel_id are Scylla snowflakes — no FK. Only the user is a real Postgres row.
        builder
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Page aggregation reads by message id.
        builder.HasIndex(r => r.MessageId);
    }
}
