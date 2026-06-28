using Harmony.Domain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Harmony.Infrastructure.Postgres.Configuration;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        // Identity maps Id as PK automatically. We just configure our extra columns.
        builder.Property(u => u.AvatarKey).HasColumnName("avatar_key");
        builder.Property(u => u.BannerKey).HasColumnName("banner_key");
        builder.Property(u => u.DateOfBirth).HasColumnName("date_of_birth");
        builder.Property(u => u.StatusMessage).HasMaxLength(128);
        builder.Property(u => u.StatusMessageExpiresAt).HasColumnName("status_message_expires_at");
        builder
            .Property(u => u.PreferredStatus)
            .HasColumnName("preferred_status")
            .HasMaxLength(16)
            .HasDefaultValue("online");
        builder
            .Property(u => u.PreferredStatusExpiresAt)
            .HasColumnName("preferred_status_expires_at");
        builder.Property(u => u.AccountStatus).HasMaxLength(16).HasDefaultValue("active");
        builder.Property(u => u.CreatedAt).IsRequired();

        // Use snake_case column names to match the schema
        builder.Property(u => u.Id).HasColumnName("id");
        builder.Property(u => u.UserName).HasColumnName("username").HasMaxLength(32);
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(255);
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash");

        // Suppress unused Identity columns so they don't pollute the migration
        builder.Ignore(u => u.PhoneNumber);
        builder.Ignore(u => u.PhoneNumberConfirmed);
        builder.Ignore(u => u.LockoutEnd);
        builder.Ignore(u => u.LockoutEnabled);
        builder.Ignore(u => u.AccessFailedCount);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.TokenHash).HasColumnName("token_hash").IsRequired();
        builder.Property(r => r.FamilyId).HasColumnName("family_id");
        builder.Property(r => r.ExpiresAt).HasColumnName("expires_at");
        builder.Property(r => r.RevokedAt).HasColumnName("revoked_at");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(r => r.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.TokenHash).IsUnique();
        builder.HasIndex(r => r.FamilyId);
        builder.HasIndex(r => r.UserId);
    }
}

public class GuildConfiguration : IEntityTypeConfiguration<Guild>
{
    public void Configure(EntityTypeBuilder<Guild> builder)
    {
        builder.ToTable("Guilds");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(g => g.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(g => g.Description).HasColumnName("description");
        builder.Property(g => g.OwnerId).HasColumnName("owner_id");
        builder.Property(g => g.IconKey).HasColumnName("icon_key");
        builder.Property(g => g.BannerKey).HasColumnName("banner_key");
        builder.Property(g => g.IsPublic).HasColumnName("is_public");
        builder.Property(g => g.MemberCount).HasColumnName("member_count");
        builder.Property(g => g.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(g => g.Owner)
            .WithMany()
            .HasForeignKey(g => g.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => g.OwnerId);
    }
}

public class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("Channels");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.GuildId).HasColumnName("guild_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(c => c.Topic).HasColumnName("topic").HasMaxLength(1024);
        builder.Property(c => c.Type).HasColumnName("type").HasMaxLength(16).IsRequired();
        builder.Property(c => c.Position).HasColumnName("position");
        builder.Property(c => c.CategoryId).HasColumnName("category_id");
        builder.Property(c => c.IsNsfw).HasColumnName("is_nsfw");
        builder.Property(c => c.SlowmodeSeconds).HasColumnName("slowmode_seconds");
        builder.Property(c => c.Bitrate).HasColumnName("bitrate");
        builder.Property(c => c.UserLimit).HasColumnName("user_limit");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        builder
            .HasOne(c => c.Guild)
            .WithMany(g => g.Channels)
            .HasForeignKey(c => c.GuildId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing for categories
        builder
            .HasOne(c => c.Category)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(c => c.GuildId);
        builder.HasIndex(c => c.CategoryId);
    }
}
