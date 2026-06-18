namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// The presigned PUT the client uploads to directly, plus the id it confirms against afterwards.
/// <c>ExpiresAt</c> is the unix-ms instant the URL stops working.
/// </summary>
public record PresignFileResponse(long FileId, string UploadUrl, string ObjectKey, long ExpiresAt);

/// <summary>A short-lived presigned URL to fetch a file's bytes directly from the store, with the
/// unix-ms instant it stops working (the client caches the URL just under that lifetime).</summary>
public record FileUrlResponse(string Url, long ExpiresAt);

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
