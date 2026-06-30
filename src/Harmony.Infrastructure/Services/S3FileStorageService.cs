using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

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
}
