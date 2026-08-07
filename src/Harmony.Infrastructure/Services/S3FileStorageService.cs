using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Blurhash.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// S3-compatible <see cref="IFileStorageService"/> (AWS SDK), pointed at MinIO locally and at
/// real S3 in production by config alone. Owns its own <see cref="IAmazonS3"/> built from the
/// <c>ObjectStorage</c> section so the SDK types stay confined to Infrastructure. Registered as a
/// singleton (the client is thread-safe and holds the connection).
///
/// MinIO specifics: <c>ForcePathStyle = true</c> (path-style bucket addressing) and the presign
/// <c>Protocol</c> is pinned to match <c>UseSSL</c> — the SDK otherwise always mints https URLs,
/// which a plain-http MinIO rejects.
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

        var endpoint = section["Endpoint"] ?? "localhost:9000";
        var accessKey = section["AccessKey"] ?? "";
        var secretKey = section["SecretKey"] ?? "";
        var useSsl = section.GetValue("UseSSL", false);
        _presignProtocol = useSsl ? Protocol.HTTPS : Protocol.HTTP;

        var config = new AmazonS3Config
        {
            ServiceURL = $"{(useSsl ? "https" : "http")}://{endpoint}",
            ForcePathStyle = true,
        };
        _client = new AmazonS3Client(accessKey, secretKey, config);
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(_client, _bucket))
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, ct);
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

    // WebP quality for the responsive srcset variants (small display copies — a touch lower than the
    // in-place original re-encode below, which stays high because it's what the lightbox serves).
    private const int VariantWebpQuality = 82;
    private const int OriginalJpegQuality = 92;
    private const int OriginalWebpQuality = 90;
    // BlurHash is O(pixels × components); compute it from a small clone, not the full-res image.
    private const int BlurHashSampleMax = 64;

    public async Task<ProcessedImage?> ProcessChatImageAsync(
        string sourceKey,
        IReadOnlyList<int> variantWidths,
        CancellationToken ct = default
    )
    {
        Image<Rgba32> image;
        try
        {
            using var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = sourceKey },
                ct
            );
            image = await Image.LoadAsync<Rgba32>(response.ResponseStream, ct);
        }
        catch (Exception ex)
        {
            // Not a decodable image — the caller treats null as a magic-byte mismatch (400).
            _logger.LogWarning(ex, "Could not decode chat image {SourceKey}", sourceKey);
            return null;
        }

        using (image)
        {
            var width = image.Width;
            var height = image.Height;
            var sourceFormat = image.Metadata.DecodedImageFormat;
            var animated = image.Frames.Count > 1;

            // BlurHash placeholder — fail-open (a null hash just means no blur, the reserved box stays).
            string? blurHash = null;
            try
            {
                using var sample = image.Clone(c =>
                    c.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(BlurHashSampleMax, BlurHashSampleMax),
                    })
                );
                blurHash = Blurhasher.Encode(sample, 4, 3);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BlurHash encode failed for {SourceKey}", sourceKey);
            }

            var generated = new List<int>();

            // Variants + EXIF strip only make sense for a single still — never flatten animation.
            if (!animated)
            {
                foreach (var w in variantWidths)
                {
                    if (w >= width)
                        continue; // never upscale
                    try
                    {
                        var targetH = Math.Max(1, (int)Math.Round(height * (double)w / width));
                        using var variant = image.Clone(c => c.Resize(w, targetH));
                        StripMetadata(variant);
                        using var buffer = new MemoryStream();
                        await variant.SaveAsync(buffer, new WebpEncoder { Quality = VariantWebpQuality }, ct);
                        buffer.Position = 0;
                        await _client.PutObjectAsync(
                            new PutObjectRequest
                            {
                                BucketName = _bucket,
                                Key = $"{sourceKey}_w{w}",
                                InputStream = buffer,
                                ContentType = "image/webp",
                            },
                            ct
                        );
                        generated.Add(w);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "WebP variant w{Width} failed for {SourceKey}", w, sourceKey);
                    }
                }

                // EXIF/metadata strip on the original (privacy — GPS etc.). Re-saved in place with
                // pixels preserved; skipped when there's nothing to strip or the format is unknown.
                try
                {
                    if (sourceFormat is not null && HasStrippableMetadata(image))
                    {
                        StripMetadata(image);
                        using var buffer = new MemoryStream();
                        await image.SaveAsync(buffer, EncoderForFormat(sourceFormat), ct);
                        buffer.Position = 0;
                        await _client.PutObjectAsync(
                            new PutObjectRequest
                            {
                                BucketName = _bucket,
                                Key = sourceKey,
                                InputStream = buffer,
                                ContentType = sourceFormat.DefaultMimeType,
                            },
                            ct
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "EXIF strip failed for {SourceKey}", sourceKey);
                }
            }

            return new ProcessedImage(width, height, blurHash, generated);
        }
    }

    private static void StripMetadata(Image image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
        // ICC left intact on purpose — dropping the colour profile can visibly shift colours.
    }

    private static bool HasStrippableMetadata(Image image) =>
        image.Metadata.ExifProfile is not null
        || image.Metadata.IptcProfile is not null
        || image.Metadata.XmpProfile is not null;

    private static ImageEncoder EncoderForFormat(IImageFormat format) =>
        format switch
        {
            JpegFormat => new JpegEncoder { Quality = OriginalJpegQuality },
            PngFormat => new PngEncoder(),
            WebpFormat => new WebpEncoder { Quality = OriginalWebpQuality },
            _ => new PngEncoder(),
        };
}
