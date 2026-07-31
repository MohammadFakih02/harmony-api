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
public sealed partial class FileService : IFileService
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

    // Server-side authoritative caps (NON-NEGOTIABLE #8 — the client-side cropper also downscales,
    // but nothing stops a raw PUT of a 4K original). Assets are capped IN PLACE; chat images keep
    // their original bytes untouched and get a separate display-only thumbnail derivative.
    // GIFs are exempt everywhere: resizing means flattening animation, and the 10 MB cap bounds them.
    public const int AvatarMaxDimension = 512; // avatars, guild icons, group-DM icons (square-ish)
    public const int BannerMaxDimension = 1280; // user + guild banners (wide)
    public const int ThumbnailMaxWidth = 800;
    public const int ThumbnailMaxHeight = 600;
    public const int ThumbnailThresholdPx = 1024; // images at/under this on both axes skip the thumb

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

            // Display-only thumbnail for large stills — the ORIGINAL is never touched (users
            // download/lightbox it at full quality). WebP: ships with ImageSharp, ~30% smaller
            // than JPEG, alpha, universal browser support. Fail-open: null just means no thumb.
            if (!file.ContentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase)
                && (d.Width > ThumbnailThresholdPx || d.Height > ThumbnailThresholdPx))
            {
                var thumbKey = $"{file.MinioKey}_thumb";
                var thumb = await _storage.DownscaleImageAsync(
                    file.MinioKey,
                    thumbKey,
                    ThumbnailMaxWidth,
                    ThumbnailMaxHeight,
                    "image/webp",
                    ct
                );
                if (thumb is not null)
                    file.ThumbnailKey = thumbKey;
            }
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

        return await MintDownloadResponseAsync(file, ct);
    }

    public async Task<List<FileDownloadResponse>> GetDownloadUrlsAsync(
        long? guildId,
        long channelId,
        IReadOnlyCollection<long> fileIds,
        CancellationToken ct = default
    )
    {
        if (fileIds.Count == 0)
            return [];

        // Same scoping rule as the single-file path, but omit instead of 404 — a page prewarm
        // shouldn't fail wholesale because one message references a since-deleted file.
        var files = await _files.GetByIdsAsync(fileIds);
        var results = new List<FileDownloadResponse>(files.Count);
        foreach (var file in files)
        {
            if (!file.IsConfirmed || file.GuildId != guildId || file.ChannelId != channelId)
                continue;
            // Presigning is a local HMAC computation (no storage round trip), so a loop is fine.
            results.Add(await MintDownloadResponseAsync(file, ct));
        }
        return results;
    }

    private async Task<FileDownloadResponse> MintDownloadResponseAsync(
        FileAttachment file,
        CancellationToken ct
    )
    {
        var url = await _storage.GetPresignedGetUrlAsync(file.MinioKey, DownloadUrlExpiry, ct);
        var thumbnailUrl = file.ThumbnailKey is { } thumbKey
            ? await _storage.GetPresignedGetUrlAsync(thumbKey, DownloadUrlExpiry, ct)
            : null;
        var expiresAt = DateTimeOffset.UtcNow.Add(DownloadUrlExpiry).ToUnixTimeMilliseconds();
        return new FileDownloadResponse(
            file.Id,
            file.Filename,
            file.ContentType,
            file.SizeBytes,
            file.Width,
            file.Height,
            url,
            expiresAt,
            thumbnailUrl
        );
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

    /// <summary>
    /// Server-side authoritative cap for profile/guild/group-DM assets: downscales the stored
    /// object IN PLACE so its longest side fits <paramref name="maxDimension"/>, then overwrites
    /// the row's dimensions/size/content-type with the re-encoded result. GIFs are skipped
    /// (animation; the 10 MB asset cap bounds them) and a null result — already-fits, animated,
    /// or any failure — keeps the original untouched (fail-open).
    /// </summary>
    private async Task CapAssetInPlaceAsync(
        FileAttachment file,
        int maxDimension,
        CancellationToken ct
    )
    {
        if (file.ContentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase))
            return;

        var result = await _storage.DownscaleImageAsync(
            file.MinioKey,
            file.MinioKey,
            maxDimension,
            maxDimension,
            encodeAsContentType: null,
            ct
        );
        if (result is null)
            return;

        // Don't re-stat: the downscale result IS the authoritative post-write state.
        file.Width = result.Width;
        file.Height = result.Height;
        file.SizeBytes = result.SizeBytes;
        file.ContentType = result.ContentType;
    }

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
