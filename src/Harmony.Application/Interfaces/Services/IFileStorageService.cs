namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Abstraction over the object store (MinIO) so the upload use case stays free of SDK types and
/// the Clean Architecture dependency rule holds. The concrete implementation lives in
/// Infrastructure. NON-NEGOTIABLE #5: MinIO is never exposed directly — clients only ever get
/// presigned URLs minted here.
/// </summary>
public interface IFileStorageService
{
    /// <summary>Creates the bucket if it does not already exist. Called once on startup.</summary>
    Task EnsureBucketAsync(CancellationToken ct = default);

    /// <summary>
    /// Mints a presigned PUT URL the client uploads to directly. <paramref name="contentType"/> is
    /// bound into the signature so the client must send the same header.
    /// </summary>
    Task<string> GetPresignedPutUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan expiry,
        CancellationToken ct = default
    );

    /// <summary>
    /// Stats the stored object — the source of truth for size/content-type after an upload.
    /// Returns null if the object does not exist (i.e. the client never completed the PUT).
    /// </summary>
    Task<StoredObjectInfo?> StatObjectAsync(string objectKey, CancellationToken ct = default);

    /// <summary>
    /// Reads the object header and decodes its pixel dimensions. Returns null if the bytes are not
    /// a decodable image — which doubles as a magic-byte check for declared image uploads.
    /// </summary>
    Task<(int Width, int Height)?> TryReadImageDimensionsAsync(
        string objectKey,
        CancellationToken ct = default
    );
}

/// <summary>The authoritative size and content-type read back from the object store.</summary>
public record StoredObjectInfo(long Size, string ContentType);
