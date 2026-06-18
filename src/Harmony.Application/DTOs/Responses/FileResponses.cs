namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// The presigned PUT the client uploads to directly, plus the id it confirms against afterwards.
/// <c>ExpiresAt</c> is the unix-ms instant the URL stops working.
/// </summary>
public record PresignFileResponse(long FileId, string UploadUrl, string ObjectKey, long ExpiresAt);

/// <summary>Everything the client needs to render a confirmed attachment: its metadata plus a
/// short-lived presigned URL to fetch the bytes directly from the store, with the unix-ms instant the
/// URL stops working (the client caches it just under that lifetime). Metadata is static; the URL is
/// the only part that expires.</summary>
public record FileDownloadResponse(
    long Id,
    string Filename,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    string Url,
    long ExpiresAt
);

public record FileAttachmentResponse(
    long Id,
    long ChannelId,
    long GuildId,
    string Filename,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    bool IsConfirmed,
    long CreatedAt
);
