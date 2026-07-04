using System.Text.Json.Serialization;

namespace Harmony.Application.DTOs.Responses;

// Snowflake ids exceed JS's safe-integer range, so they're emitted as JSON strings (matching the
// SignalR contract and the frontend models). Timestamps/sizes stay as numbers — they're within range.

/// <summary>
/// The presigned PUT the client uploads to directly, plus the id it confirms against afterwards.
/// <c>ExpiresAt</c> is the unix-ms instant the URL stops working.
/// </summary>
public record PresignFileResponse(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long FileId,
    string UploadUrl,
    string ObjectKey,
    long ExpiresAt
);

/// <summary>Everything the client needs to render a confirmed attachment: its metadata plus a
/// short-lived presigned URL to fetch the bytes directly from the store, with the unix-ms instant the
/// URL stops working (the client caches it just under that lifetime). Metadata is static; the URL is
/// the only part that expires.</summary>
public record FileDownloadResponse(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long Id,
    string Filename,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    string Url,
    long ExpiresAt
);

public record FileAttachmentResponse(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long Id,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long? ChannelId,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] long? GuildId,
    string Filename,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    bool IsConfirmed,
    long CreatedAt
);

// Returned when a user asset (avatar/banner) upload is confirmed — the storage key now set on the
// profile. The client renders it through GET /api/files/public/{key}.
public record UserAssetResponse(string Key);
