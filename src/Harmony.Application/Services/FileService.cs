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
///   - Allowlist is images-only this branch, so ImageSharp's decode at confirm is a real magic-byte
///     check: a file that declares an image type but does not decode is rejected.
/// </summary>
public sealed class FileService : IFileService
{
    public const long MaxFileSizeBytes = 50L * 1024 * 1024; // 50 MB

    private static readonly TimeSpan PresignExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DownloadUrlExpiry = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
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
        long guildId,
        long channelId,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        // Channel-existence 404 before any other check (mirrors MessageService).
        var channel = await _channels.GetByIdAndGuildIdAsync(channelId, guildId);
        if (channel is null)
            throw new KeyNotFoundException("Channel not found.");

        if (!AllowedContentTypes.Contains(request.ContentType))
            throw new ArgumentException($"Content type '{request.ContentType}' is not allowed.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxFileSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        var fileId = _snowflake.NextId();
        var objectKey = $"attachments/{guildId}/{channelId}/{fileId}";

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

        // The allowlist is images-only, so the decode below is also the magic-byte check.
        var dims = await _storage.TryReadImageDimensionsAsync(file.MinioKey, ct);
        if (dims is not { } d)
            throw new ArgumentException("Uploaded object is not a valid image.");

        file.Width = d.Width;
        file.Height = d.Height;
        file.IsConfirmed = true;
        await _files.SaveChangesAsync();

        return ToResponse(file);
    }

    public async Task<FileUrlResponse> GetDownloadUrlAsync(
        long guildId,
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
        return new FileUrlResponse(url, expiresAt);
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
