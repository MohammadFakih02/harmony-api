using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;

namespace Harmony.Application.Services;

/// <summary>
/// The file-upload use case (presign → confirm). AttachFiles is enforced at the route by the
/// permission filter, so it is not re-checked here. The two write paths mirror MessageService:
/// channel-existence 404 runs before anything else, and failures throw the exception types the
/// GlobalExceptionHandler maps (KeyNotFound→404, Unauthorized→403, Argument→400).
///
/// Decisions (see plan):
///   - Object key: attachments/{guildId}/{channelId}/{fileId} — filename is stored in the row, not
///     the key, so it never needs URL-encoding.
///   - The client's declared size/content-type only gate the presign; confirm overwrites them with
///     the object store's authoritative values (NON-NEGOTIABLE #8 — never trust client claims).
///   - Confirm verifies the object's actual bytes match the declared type: images go through
///     ImageSharp's decode (which also yields dimensions), every other allowed type through the
///     pure <see cref="FileSignatures"/> magic-byte sniff. A file declaring a type its bytes don't
///     match is rejected.
/// </summary>
public sealed class FileService : IFileService
{
    public const long MaxFileSizeBytes = 50L * 1024 * 1024; // 50 MB

    // Bytes pulled at confirm for the non-image magic-byte sniff (a range request).
    private const int SniffByteCount = 64;

    private static readonly TimeSpan PresignExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DownloadUrlExpiry = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images (validated by ImageSharp decode at confirm).
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        // Video.
        "video/mp4",
        "video/webm",
        "video/quicktime",
        // Audio.
        "audio/mpeg",
        "audio/ogg",
        "audio/wav",
        "audio/webm",
        // Documents.
        "application/pdf",
        "text/plain",
        "text/csv",
        "text/markdown",
        // Archives.
        "application/zip",
    };

    // Profile assets (avatar/banner) are images only and much smaller than chat attachments.
    public const long MaxUserAssetSizeBytes = 10L * 1024 * 1024; // 10 MB

    private static readonly HashSet<string> UserAssetContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    };

    private readonly IFileAttachmentRepository _files;
    private readonly IChannelRepository _channels;
    private readonly IUserRepository _users;
    private readonly IGuildRepository _guilds;
    private readonly IFriendRepository _friends;
    private readonly IFileStorageService _storage;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IHubBroadcaster _broadcaster;

    public FileService(
        IFileAttachmentRepository files,
        IChannelRepository channels,
        IUserRepository users,
        IGuildRepository guilds,
        IFriendRepository friends,
        IFileStorageService storage,
        ISnowflakeIdGenerator snowflake,
        IHubBroadcaster broadcaster
    )
    {
        _files = files;
        _channels = channels;
        _users = users;
        _guilds = guilds;
        _friends = friends;
        _storage = storage;
        _snowflake = snowflake;
        _broadcaster = broadcaster;
    }

    public async Task<PresignFileResponse> PresignAsync(
        long userId,
        long? guildId,
        long channelId,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        // Channel-existence 404 before any other check (mirrors MessageService). A guild upload
        // must resolve in that guild; a DM upload must be a guild-less "dm" channel. (Participant
        // authorization for DMs is enforced by the controller, mirroring how AttachFiles is
        // enforced at the guild route.)
        if (guildId is { } gid)
        {
            if (await _channels.GetByIdAndGuildIdAsync(channelId, gid) is null)
                throw new KeyNotFoundException("Channel not found.");
        }
        else
        {
            var channel = await _channels.GetByIdAsync(channelId);
            if (channel is null
                || channel.GuildId is not null
                || (channel.Type != "dm" && channel.Type != "group_dm"))
                throw new KeyNotFoundException("Channel not found.");
        }

        if (!AllowedContentTypes.Contains(request.ContentType))
            throw new ArgumentException($"Content type '{request.ContentType}' is not allowed.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxFileSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        var fileId = _snowflake.NextId();
        // DMs have no guild — bucket them under a "dm" segment so the key never contains an empty path.
        var scope = guildId is { } g ? g.ToString() : "dm";
        var objectKey = $"attachments/{scope}/{channelId}/{fileId}";

        await _files.AddAsync(
            new FileAttachment
            {
                Id = fileId,
                UploaderId = userId,
                GuildId = guildId,
                ChannelId = channelId,
                MinioKey = objectKey,
                Filename = request.Filename,
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes,
                IsConfirmed = false,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await _files.SaveChangesAsync();

        var url = await _storage.GetPresignedPutUrlAsync(
            objectKey,
            request.ContentType,
            PresignExpiry,
            ct
        );

        var expiresAt = DateTimeOffset.UtcNow.Add(PresignExpiry).ToUnixTimeMilliseconds();
        return new PresignFileResponse(fileId, url, objectKey, expiresAt);
    }

    public async Task<FileAttachmentResponse> ConfirmAsync(
        long userId,
        long fileId,
        CancellationToken ct = default
    )
    {
        var file = await _files.GetByIdAsync(fileId);
        if (file is null)
            throw new KeyNotFoundException("File not found.");

        // Never trust a client-supplied id for ownership (NON-NEGOTIABLE #8).
        if (file.UploaderId != userId)
            throw new UnauthorizedAccessException("You did not upload this file.");

        // Idempotent: a retried confirm on an already-finalized file just echoes it back.
        if (file.IsConfirmed)
            return ToResponse(file);

        var stat = await _storage.StatObjectAsync(file.MinioKey, ct);
        if (stat is null)
            throw new ArgumentException("Uploaded object was not found in storage.");

        if (stat.Size <= 0 || stat.Size > MaxFileSizeBytes)
            throw new ArgumentException("Uploaded object exceeds the maximum allowed size.");

        // Authoritative values from the store override the client's declared ones.
        file.SizeBytes = stat.Size;
        file.ContentType = stat.ContentType;

        // Defense-in-depth: the presign already gated the type, but re-check the store's
        // authoritative content-type before trusting it.
        if (!AllowedContentTypes.Contains(file.ContentType))
            throw new ArgumentException($"Content type '{file.ContentType}' is not allowed.");

        // Verify the bytes actually match the declared type. Images decode through ImageSharp
        // (which also yields dimensions); every other type is sniffed by its magic bytes.
        if (FileSignatures.IsImage(file.ContentType))
        {
            var dims = await _storage.TryReadImageDimensionsAsync(file.MinioKey, ct);
            if (dims is not { } d)
                throw new ArgumentException("Uploaded object is not a valid image.");

            file.Width = d.Width;
            file.Height = d.Height;
        }
        else
        {
            var head = await _storage.ReadObjectHeadAsync(file.MinioKey, SniffByteCount, ct);
            if (head is null || !FileSignatures.IsConsistent(file.ContentType, head))
                throw new ArgumentException("Uploaded file does not match its declared type.");
        }

        file.IsConfirmed = true;
        await _files.SaveChangesAsync();

        return ToResponse(file);
    }

    public async Task<FileDownloadResponse> GetDownloadUrlAsync(
        long? guildId,
        long channelId,
        long fileId,
        CancellationToken ct = default
    )
    {
        var file = await _files.GetByIdAsync(fileId);

        // 404 (not 403) for anything that isn't a confirmed file in this exact channel: don't leak
        // existence or pending uploads, and keep a file scoped to its own channel. ViewChannel on the
        // route channel is already enforced by the [RequirePermission] filter.
        if (file is null || !file.IsConfirmed || file.GuildId != guildId || file.ChannelId != channelId)
            throw new KeyNotFoundException("File not found.");

        var url = await _storage.GetPresignedGetUrlAsync(file.MinioKey, DownloadUrlExpiry, ct);
        var expiresAt = DateTimeOffset.UtcNow.Add(DownloadUrlExpiry).ToUnixTimeMilliseconds();
        return new FileDownloadResponse(
            file.Id,
            file.Filename,
            file.ContentType,
            file.SizeBytes,
            file.Width,
            file.Height,
            url,
            expiresAt
        );
    }

    public async Task<PresignFileResponse> PresignUserAssetAsync(
        long userId,
        string kind,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        var prefix = AssetPrefix(kind);

        if (!UserAssetContentTypes.Contains(request.ContentType))
            throw new ArgumentException("Avatars and banners must be png, jpeg, gif, or webp.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxUserAssetSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        var fileId = _snowflake.NextId();
        var objectKey = $"{prefix}/{userId}/{fileId}";

        await _files.AddAsync(
            new FileAttachment
            {
                Id = fileId,
                UploaderId = userId,
                GuildId = null,
                ChannelId = null, // profile assets have no channel container
                MinioKey = objectKey,
                Filename = request.Filename,
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes,
                IsConfirmed = false,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await _files.SaveChangesAsync();

        var url = await _storage.GetPresignedPutUrlAsync(
            objectKey,
            request.ContentType,
            PresignExpiry,
            ct
        );

        var expiresAt = DateTimeOffset.UtcNow.Add(PresignExpiry).ToUnixTimeMilliseconds();
        return new PresignFileResponse(fileId, url, objectKey, expiresAt);
    }

    public async Task<UserAssetResponse> ConfirmUserAssetAsync(
        long userId,
        string kind,
        long fileId,
        CancellationToken ct = default
    )
    {
        var prefix = AssetPrefix(kind);

        var file = await _files.GetByIdAsync(fileId);
        // A chat attachment (or the other asset kind) can never be confirmed through this path —
        // 404 like any unknown id, don't leak that the row exists.
        if (file is null || !file.MinioKey.StartsWith($"{prefix}/", StringComparison.Ordinal))
            throw new KeyNotFoundException("File not found.");

        if (file.UploaderId != userId)
            throw new UnauthorizedAccessException("You did not upload this file.");

        if (!file.IsConfirmed)
        {
            var stat = await _storage.StatObjectAsync(file.MinioKey, ct);
            if (stat is null)
                throw new ArgumentException("Uploaded object was not found in storage.");

            if (stat.Size <= 0 || stat.Size > MaxUserAssetSizeBytes)
                throw new ArgumentException("Uploaded object exceeds the maximum allowed size.");

            file.SizeBytes = stat.Size;
            file.ContentType = stat.ContentType;

            if (!UserAssetContentTypes.Contains(file.ContentType))
                throw new ArgumentException("Avatars and banners must be png, jpeg, gif, or webp.");

            // Profile assets are always images — a successful decode IS the byte validation.
            var dims = await _storage.TryReadImageDimensionsAsync(file.MinioKey, ct);
            if (dims is not { } d)
                throw new ArgumentException("Uploaded object is not a valid image.");

            file.Width = d.Width;
            file.Height = d.Height;
            file.IsConfirmed = true;
        }

        var user = await _users.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        var oldKey = kind == "avatar" ? user.AvatarKey : user.BannerKey;
        if (kind == "avatar")
            user.AvatarKey = file.MinioKey;
        else
            user.BannerKey = file.MinioKey;

        // Retire the replaced asset (object + row), best-effort — same posture as the orphan
        // sweep: an object-delete failure never fails the confirm, the row is reclaimed anyway.
        if (oldKey is not null && oldKey != file.MinioKey
            && oldKey.StartsWith($"{prefix}/", StringComparison.Ordinal))
        {
            try
            {
                await _storage.DeleteObjectAsync(oldKey, ct);
            }
            catch
            {
                // best-effort — a stale object in the store is harmless
            }

            if (TryParseAssetFileId(oldKey, out var oldFileId)
                && await _files.GetByIdAsync(oldFileId) is { } oldRow)
            {
                _files.RemoveRange([oldRow]);
            }
        }

        // Repos share the scoped DbContext — one save commits the file + user + row removal.
        await _files.SaveChangesAsync();

        // Live-push the new avatar so member lists / chat / DMs update without a refetch (banner
        // isn't rendered anywhere that live-updates, so only avatar changes are broadcast).
        if (kind == "avatar")
            await BroadcastAvatarUpdatedAsync(userId, file.MinioKey, ct);

        return new UserAssetResponse(file.MinioKey);
    }

    public async Task RemoveUserAssetAsync(long userId, string kind, CancellationToken ct = default)
    {
        var prefix = AssetPrefix(kind);

        var user = await _users.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        var key = kind == "avatar" ? user.AvatarKey : user.BannerKey;
        if (key is null)
            return; // idempotent — nothing to remove

        if (kind == "avatar")
            user.AvatarKey = null;
        else
            user.BannerKey = null;

        if (key.StartsWith($"{prefix}/", StringComparison.Ordinal))
        {
            try
            {
                await _storage.DeleteObjectAsync(key, ct);
            }
            catch
            {
                // best-effort — see ConfirmUserAssetAsync
            }

            if (TryParseAssetFileId(key, out var fileId)
                && await _files.GetByIdAsync(fileId) is { } row)
            {
                _files.RemoveRange([row]);
            }
        }

        await _files.SaveChangesAsync();

        if (kind == "avatar")
            await BroadcastAvatarUpdatedAsync(userId, null, ct);
    }

    /// <summary>
    /// Fans an avatar change out to the surfaces that render it live: every guild the user belongs
    /// to (guild groups), their friends, and their own tabs. Best-effort — a broadcast failure must
    /// never fail the upload/removal it follows (the change is already committed).
    /// </summary>
    private async Task BroadcastAvatarUpdatedAsync(long userId, string? avatarKey, CancellationToken ct)
    {
        try
        {
            var payload = new ProfileUpdatedPayload(userId, avatarKey);

            var guildIds = await _guilds.GetGuildIdsForUserAsync(userId);
            foreach (var guildId in guildIds)
                await _broadcaster.BroadcastProfileUpdatedToGuildAsync(guildId, payload, ct);

            // The user's own tabs (deck avatar) + each friend (DM list / friends list).
            await _broadcaster.BroadcastProfileUpdatedToUserAsync(userId, payload, ct);
            var friendIds = await _friends.GetFriendIdsAsync(userId);
            foreach (var friendId in friendIds)
                await _broadcaster.BroadcastProfileUpdatedToUserAsync(friendId, payload, ct);
        }
        catch
        {
            // best-effort — see summary
        }
    }

    // ---- guild assets (icon/banner) — same pipeline, guild-scoped, ManageGuild at the route ----

    public async Task<PresignFileResponse> PresignGuildAssetAsync(
        long actorId,
        long guildId,
        string kind,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        var prefix = GuildAssetPrefix(kind);

        if (!UserAssetContentTypes.Contains(request.ContentType))
            throw new ArgumentException("Icons and banners must be png, jpeg, gif, or webp.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxUserAssetSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        if (await _guilds.GetByIdAsync(guildId) is null)
            throw new KeyNotFoundException("Guild not found.");

        var fileId = _snowflake.NextId();
        var objectKey = $"{prefix}/{guildId}/{fileId}";

        await _files.AddAsync(
            new FileAttachment
            {
                Id = fileId,
                UploaderId = actorId,
                GuildId = guildId,
                ChannelId = null, // guild assets have no channel container
                MinioKey = objectKey,
                Filename = request.Filename,
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes,
                IsConfirmed = false,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await _files.SaveChangesAsync();

        var url = await _storage.GetPresignedPutUrlAsync(
            objectKey,
            request.ContentType,
            PresignExpiry,
            ct
        );

        var expiresAt = DateTimeOffset.UtcNow.Add(PresignExpiry).ToUnixTimeMilliseconds();
        return new PresignFileResponse(fileId, url, objectKey, expiresAt);
    }

    public async Task<UserAssetResponse> ConfirmGuildAssetAsync(
        long guildId,
        string kind,
        long fileId,
        CancellationToken ct = default
    )
    {
        var prefix = GuildAssetPrefix(kind);

        var file = await _files.GetByIdAsync(fileId);
        // The key must sit under THIS guild's asset prefix — a chat attachment, profile asset, or
        // another guild's asset can never be confirmed through this route. 404, don't leak.
        if (file is null || !file.MinioKey.StartsWith($"{prefix}/{guildId}/", StringComparison.Ordinal))
            throw new KeyNotFoundException("File not found.");

        if (!file.IsConfirmed)
        {
            var stat = await _storage.StatObjectAsync(file.MinioKey, ct);
            if (stat is null)
                throw new ArgumentException("Uploaded object was not found in storage.");

            if (stat.Size <= 0 || stat.Size > MaxUserAssetSizeBytes)
                throw new ArgumentException("Uploaded object exceeds the maximum allowed size.");

            file.SizeBytes = stat.Size;
            file.ContentType = stat.ContentType;

            if (!UserAssetContentTypes.Contains(file.ContentType))
                throw new ArgumentException("Icons and banners must be png, jpeg, gif, or webp.");

            var dims = await _storage.TryReadImageDimensionsAsync(file.MinioKey, ct);
            if (dims is not { } d)
                throw new ArgumentException("Uploaded object is not a valid image.");

            file.Width = d.Width;
            file.Height = d.Height;
            file.IsConfirmed = true;
        }

        var guild = await _guilds.GetByIdAsync(guildId)
            ?? throw new KeyNotFoundException("Guild not found.");

        var oldKey = kind == "icon" ? guild.IconKey : guild.BannerKey;
        if (kind == "icon")
            guild.IconKey = file.MinioKey;
        else
            guild.BannerKey = file.MinioKey;

        if (oldKey is not null && oldKey != file.MinioKey
            && oldKey.StartsWith($"{prefix}/", StringComparison.Ordinal))
        {
            try
            {
                await _storage.DeleteObjectAsync(oldKey, ct);
            }
            catch
            {
                // best-effort — a stale object in the store is harmless
            }

            if (TryParseAssetFileId(oldKey, out var oldFileId)
                && await _files.GetByIdAsync(oldFileId) is { } oldRow)
            {
                _files.RemoveRange([oldRow]);
            }
        }

        await _files.SaveChangesAsync();

        return new UserAssetResponse(file.MinioKey);
    }

    public async Task RemoveGuildAssetAsync(long guildId, string kind, CancellationToken ct = default)
    {
        var prefix = GuildAssetPrefix(kind);

        var guild = await _guilds.GetByIdAsync(guildId)
            ?? throw new KeyNotFoundException("Guild not found.");

        var key = kind == "icon" ? guild.IconKey : guild.BannerKey;
        if (key is null)
            return; // idempotent

        if (kind == "icon")
            guild.IconKey = null;
        else
            guild.BannerKey = null;

        if (key.StartsWith($"{prefix}/", StringComparison.Ordinal))
        {
            try
            {
                await _storage.DeleteObjectAsync(key, ct);
            }
            catch
            {
                // best-effort — see ConfirmGuildAssetAsync
            }

            if (TryParseAssetFileId(key, out var fileId)
                && await _files.GetByIdAsync(fileId) is { } row)
            {
                _files.RemoveRange([row]);
            }
        }

        await _files.SaveChangesAsync();
    }

    private static string GuildAssetPrefix(string kind) =>
        kind switch
        {
            "icon" => "guild-icons",
            "banner" => "guild-banners",
            _ => throw new ArgumentException("Asset kind must be 'icon' or 'banner'."),
        };

    // ---- group-DM icon — same pipeline, channel-scoped, participant-gated at the route ----

    private const string ChannelIconPrefix = "channel-icons";

    public async Task<PresignFileResponse> PresignGroupDmIconAsync(
        long actorId,
        long channelId,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        if (!UserAssetContentTypes.Contains(request.ContentType))
            throw new ArgumentException("Icons must be png, jpeg, gif, or webp.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxUserAssetSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.Type != "group_dm")
            throw new KeyNotFoundException("Channel not found.");

        var fileId = _snowflake.NextId();
        var objectKey = $"{ChannelIconPrefix}/{channelId}/{fileId}";

        await _files.AddAsync(
            new FileAttachment
            {
                Id = fileId,
                UploaderId = actorId,
                // Both null: asset rows are identified by key prefix only, and a null ChannelId
                // keeps the row un-attachable through the message send path.
                GuildId = null,
                ChannelId = null,
                MinioKey = objectKey,
                Filename = request.Filename,
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes,
                IsConfirmed = false,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await _files.SaveChangesAsync();

        var url = await _storage.GetPresignedPutUrlAsync(
            objectKey,
            request.ContentType,
            PresignExpiry,
            ct
        );

        var expiresAt = DateTimeOffset.UtcNow.Add(PresignExpiry).ToUnixTimeMilliseconds();
        return new PresignFileResponse(fileId, url, objectKey, expiresAt);
    }

    public async Task<UserAssetResponse> ConfirmGroupDmIconAsync(
        long channelId,
        long fileId,
        CancellationToken ct = default
    )
    {
        var file = await _files.GetByIdAsync(fileId);
        // The key must sit under THIS channel's icon prefix — a chat attachment, profile asset,
        // or another channel's icon can never be confirmed through this route. 404, don't leak.
        if (file is null
            || !file.MinioKey.StartsWith($"{ChannelIconPrefix}/{channelId}/", StringComparison.Ordinal))
            throw new KeyNotFoundException("File not found.");

        if (!file.IsConfirmed)
        {
            var stat = await _storage.StatObjectAsync(file.MinioKey, ct);
            if (stat is null)
                throw new ArgumentException("Uploaded object was not found in storage.");

            if (stat.Size <= 0 || stat.Size > MaxUserAssetSizeBytes)
                throw new ArgumentException("Uploaded object exceeds the maximum allowed size.");

            file.SizeBytes = stat.Size;
            file.ContentType = stat.ContentType;

            if (!UserAssetContentTypes.Contains(file.ContentType))
                throw new ArgumentException("Icons must be png, jpeg, gif, or webp.");

            var dims = await _storage.TryReadImageDimensionsAsync(file.MinioKey, ct);
            if (dims is not { } d)
                throw new ArgumentException("Uploaded object is not a valid image.");

            file.Width = d.Width;
            file.Height = d.Height;
            file.IsConfirmed = true;
        }

        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.Type != "group_dm")
            throw new KeyNotFoundException("Channel not found.");

        var oldKey = channel.IconKey;
        channel.IconKey = file.MinioKey;

        if (oldKey is not null && oldKey != file.MinioKey
            && oldKey.StartsWith($"{ChannelIconPrefix}/", StringComparison.Ordinal))
        {
            try
            {
                await _storage.DeleteObjectAsync(oldKey, ct);
            }
            catch
            {
                // best-effort — a stale object in the store is harmless
            }

            if (TryParseAssetFileId(oldKey, out var oldFileId)
                && await _files.GetByIdAsync(oldFileId) is { } oldRow)
            {
                _files.RemoveRange([oldRow]);
            }
        }

        await _files.SaveChangesAsync();

        return new UserAssetResponse(file.MinioKey);
    }

    public async Task RemoveGroupDmIconAsync(long channelId, CancellationToken ct = default)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.Type != "group_dm")
            throw new KeyNotFoundException("Channel not found.");

        var key = channel.IconKey;
        if (key is null)
            return; // idempotent

        channel.IconKey = null;

        if (key.StartsWith($"{ChannelIconPrefix}/", StringComparison.Ordinal))
        {
            try
            {
                await _storage.DeleteObjectAsync(key, ct);
            }
            catch
            {
                // best-effort — see ConfirmGroupDmIconAsync
            }

            if (TryParseAssetFileId(key, out var fileId)
                && await _files.GetByIdAsync(fileId) is { } row)
            {
                _files.RemoveRange([row]);
            }
        }

        await _files.SaveChangesAsync();
    }

    public async Task<string> GetPublicFileUrlAsync(string key, CancellationToken ct = default)
    {
        // Only profile/guild/group-DM assets are publicly servable — chat attachments stay
        // channel-gated.
        if (!key.StartsWith("avatars/", StringComparison.Ordinal)
            && !key.StartsWith("banners/", StringComparison.Ordinal)
            && !key.StartsWith("guild-icons/", StringComparison.Ordinal)
            && !key.StartsWith("guild-banners/", StringComparison.Ordinal)
            && !key.StartsWith($"{ChannelIconPrefix}/", StringComparison.Ordinal))
            throw new KeyNotFoundException("File not found.");

        if (!TryParseAssetFileId(key, out var fileId))
            throw new KeyNotFoundException("File not found.");

        var file = await _files.GetByIdAsync(fileId);
        if (file is null || !file.IsConfirmed || file.MinioKey != key)
            throw new KeyNotFoundException("File not found.");

        return await _storage.GetPresignedGetUrlAsync(file.MinioKey, DownloadUrlExpiry, ct);
    }

    private static string AssetPrefix(string kind) =>
        kind switch
        {
            "avatar" => "avatars",
            "banner" => "banners",
            _ => throw new ArgumentException("Asset kind must be 'avatar' or 'banner'."),
        };

    /// <summary>The trailing path segment of an asset key is its FileAttachment snowflake id.</summary>
    private static bool TryParseAssetFileId(string key, out long fileId)
    {
        var lastSlash = key.LastIndexOf('/');
        return long.TryParse(key.AsSpan(lastSlash + 1), out fileId);
    }

    private static FileAttachmentResponse ToResponse(FileAttachment f) =>
        new(
            f.Id,
            f.ChannelId,
            f.GuildId,
            f.Filename,
            f.ContentType,
            f.SizeBytes,
            f.Width,
            f.Height,
            f.IsConfirmed,
            f.CreatedAt
        );
}
