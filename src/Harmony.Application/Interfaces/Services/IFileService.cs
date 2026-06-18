using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// The file-upload use case: mint a presigned PUT for a pending attachment, then confirm it once
/// the client has uploaded directly to the object store. AttachFiles is enforced at the route by
/// the permission filter; ownership of a pending file is enforced here at confirm time.
/// </summary>
public interface IFileService
{
    /// <summary>
    /// Validates the request, records a pending <c>FileAttachment</c>, and returns a presigned PUT
    /// URL. Throws <see cref="KeyNotFoundException"/> if the channel is missing or not in the guild,
    /// <see cref="ArgumentException"/> if the type/size is rejected.
    /// </summary>
    Task<PresignFileResponse> PresignAsync(
        long userId,
        long guildId,
        long channelId,
        PresignFileRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// Verifies the uploaded object actually exists, finalizes the row from the store's authoritative
    /// size/content-type (and image dimensions), and marks it confirmed. Idempotent. Throws
    /// <see cref="KeyNotFoundException"/> if unknown, <see cref="UnauthorizedAccessException"/> if the
    /// caller is not the uploader, <see cref="ArgumentException"/> if the object is missing or invalid.
    /// </summary>
    Task<FileAttachmentResponse> ConfirmAsync(
        long userId,
        long fileId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Mints a short-lived presigned GET URL for a confirmed file in the given channel. ViewChannel
    /// is enforced by the route filter; this verifies the file exists, is confirmed, and actually
    /// belongs to that channel (otherwise <see cref="KeyNotFoundException"/> — never leak existence
    /// or pending uploads, and keep files scoped to their channel).
    /// </summary>
    Task<FileUrlResponse> GetDownloadUrlAsync(
        long guildId,
        long channelId,
        long fileId,
        CancellationToken ct = default
    );
}
