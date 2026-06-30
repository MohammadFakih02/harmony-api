using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
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

    private readonly IFileAttachmentRepository _files;
    private readonly IChannelRepository _channels;
    private readonly IFileStorageService _storage;
    private readonly ISnowflakeIdGenerator _snowflake;

    public FileService(
        IFileAttachmentRepository files,
        IChannelRepository channels,
        IFileStorageService storage,
        ISnowflakeIdGenerator snowflake
    )
    {
        _files = files;
        _channels = channels;
        _storage = storage;
        _snowflake = snowflake;
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
            if (channel is null || channel.GuildId is not null || channel.Type != "dm")
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
