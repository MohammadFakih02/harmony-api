using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// S3-compatible <see cref="IFileStorageService"/> (AWS SDK), pointed at MinIO locally and at
/// real S3 in production by config alone. Owns its own <see cref="IAmazonS3"/> built from the
/// <c>ObjectStorage</c> section so the SDK types stay confined to Infrastructure. Registered as a
/// singleton (the client is thread-safe and holds the connection).
///
/// Two modes, chosen by whether <c>ObjectStorage:Region</c> is set. <b>Real S3</b> (region set):
/// region-routed, virtual-hosted addressing, presigns always https. <b>S3-compatible</b> (region
/// absent — MinIO in dev/test): explicit <c>ServiceURL</c> + <c>ForcePathStyle = true</c>, and the
/// presign <c>Protocol</c> is pinned to match <c>UseSSL</c>, since the SDK otherwise always mints
/// https URLs, which a plain-http MinIO rejects.
///
/// Credentials follow the same split: explicit keys when configured, otherwise the SDK's default
/// credential chain — which on EC2 resolves the instance role, so production needs no long-lived
/// secrets on the host.
/// </summary>
public sealed class S3FileStorageService : IFileStorageService
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly Protocol _presignProtocol;
    private readonly ILogger<S3FileStorageService> _logger;

    public S3FileStorageService(
        IConfiguration configuration,
        ILogger<S3FileStorageService> logger
    )
    {
        _logger = logger;
        var section = configuration.GetSection("ObjectStorage");
        _bucket = section["BucketName"] ?? "harmony";

        var region = section["Region"];
        var accessKey = section["AccessKey"] ?? "";
        var secretKey = section["SecretKey"] ?? "";
        var useSsl = section.GetValue("UseSSL", false);

        AmazonS3Config config;
        if (!string.IsNullOrWhiteSpace(region))
        {
            // Real AWS S3 — the SDK routes by region and mints virtual-hosted https URLs.
            config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
            _presignProtocol = Protocol.HTTPS;
        }
        else
        {
            // S3-compatible (MinIO): address it explicitly, path-style, and match its scheme.
            var endpoint = section["Endpoint"] ?? "localhost:9000";
            config = new AmazonS3Config
            {
                ServiceURL = $"{(useSsl ? "https" : "http")}://{endpoint}",
                ForcePathStyle = true,
            };
            _presignProtocol = useSsl ? Protocol.HTTPS : Protocol.HTTP;
        }

        // No configured key => default credential chain (the EC2 instance role in production).
        _client = string.IsNullOrWhiteSpace(accessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(accessKey, secretKey, config);
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(_client, _bucket))
            // UseClientRegion sends the LocationConstraint that every region except us-east-1
            // requires; MinIO ignores it.
            await _client.PutBucketAsync(
                new PutBucketRequest { BucketName = _bucket, UseClientRegion = true },
                ct
            );
    }

    public Task<string> GetPresignedPutUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan expiry,
        CancellationToken ct = default
    ) =>
        _client.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = objectKey,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.Add(expiry),
                // Bound into the signature — the client's PUT must send this exact Content-Type.
                ContentType = contentType,
                Protocol = _presignProtocol,
            }
        );

    public Task<string> GetPresignedGetUrlAsync(
        string objectKey,
        TimeSpan expiry,
        CancellationToken ct = default
    ) =>
        _client.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = objectKey,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(expiry),
                Protocol = _presignProtocol,
            }
        );

    public async Task<StoredObjectInfo?> StatObjectAsync(
        string objectKey,
        CancellationToken ct = default
    )
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = objectKey },
                ct
            );
            return new StoredObjectInfo(meta.ContentLength, meta.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The client never completed the PUT — a confirm-before-upload, not an error.
            return null;
        }
    }

    public async Task<(int Width, int Height)?> TryReadImageDimensionsAsync(
        string objectKey,
        CancellationToken ct = default
    )
    {
        try
        {
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = objectKey },
                ct
            );
            await using var stream = response.ResponseStream;
            var info = await Image.IdentifyAsync(stream, ct);
            return (info.Width, info.Height);
        }
        catch (Exception ex)
        {
            // Not a decodable image (or unreadable) — treated by the caller as a magic-byte
            // mismatch and surfaced as a 400.
            _logger.LogWarning(ex, "Could not read image dimensions for {ObjectKey}", objectKey);
            return null;
        }
    }

    public async Task<byte[]?> ReadObjectHeadAsync(
        string objectKey,
        int maxBytes,
        CancellationToken ct = default
    )
    {
        try
        {
            // Range request so we only pull the header, never the whole (up to 50 MB) object.
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _bucket,
                    Key = objectKey,
                    ByteRange = new ByteRange(0, maxBytes - 1),
                },
                ct
            );
            await using var stream = response.ResponseStream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default) =>
        // S3/MinIO DeleteObject is idempotent — deleting a missing key returns success.
        _client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = _bucket, Key = objectKey },
            ct
        );

    public async Task<StoredImageResult?> DownscaleImageAsync(
        string sourceKey,
        string targetKey,
        int maxWidth,
        int maxHeight,
        string? encodeAsContentType = null,
        CancellationToken ct = default
    )
    {
        try
        {
            // Full-object buffering — same class of cost as TryReadImageDimensionsAsync, and asset
            // uploads are capped at 10 MB / chat images rarely near the 50 MB cap.
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = sourceKey },
                ct
            );
            using var image = await Image.LoadAsync(response.ResponseStream, ct);

            // Never flatten animation (GIF/animated WebP) to a single frame.
            if (image.Frames.Count > 1)
                return null;

            var fits = image.Width <= maxWidth && image.Height <= maxHeight;
            // In-place cap on an image that already fits: nothing to do, keep the original bytes.
            if (fits && targetKey == sourceKey)
                return null;

            if (!fits)
            {
                image.Mutate(op =>
                    op.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max, // fit within the box, aspect preserved
                        Size = new Size(maxWidth, maxHeight),
                    })
                );
            }

            var contentType = encodeAsContentType
                ?? image.Metadata.DecodedImageFormat?.DefaultMimeType
                ?? "image/png";
            ImageEncoder encoder = contentType.ToLowerInvariant() switch
            {
                "image/webp" => new WebpEncoder { Quality = 85 },
                "image/jpeg" => new JpegEncoder { Quality = 85 },
                _ => new PngEncoder(),
            };

            using var buffer = new MemoryStream();
            await image.SaveAsync(buffer, encoder, ct);
            // Capture before the Put — the SDK auto-closes InputStream, after which Length throws.
            var sizeBytes = buffer.Length;
            buffer.Position = 0;

            await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = targetKey,
                    InputStream = buffer,
                    ContentType = contentType,
                },
                ct
            );

            return new StoredImageResult(image.Width, image.Height, sizeBytes, contentType);
        }
        catch (Exception ex)
        {
            // Fail-open: a resize failure must never fail the confirm it serves — the caller
            // falls back to the original object.
            _logger.LogWarning(
                ex,
                "Image downscale failed for {SourceKey} -> {TargetKey}",
                sourceKey,
                targetKey
            );
            return null;
        }
    }
}
