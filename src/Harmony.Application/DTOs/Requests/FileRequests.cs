using System.Text.Json.Serialization;

namespace Harmony.Application.DTOs.Requests;

/// <summary>
/// Client-declared metadata for an upload. The size/type are validated up front to mint the
/// presigned URL, but are re-verified against the object store's authoritative values at confirm —
/// the client's claims are never trusted as final.
/// </summary>
public record PresignFileRequest(string Filename, string ContentType, long SizeBytes);

/// <summary>
/// One-round-trip download-URL minting for every attachment on a loaded message page — the client
/// prewarms its URL cache with this before rendering, instead of a request per attachment.
/// </summary>
public record BatchFileDownloadRequest(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] List<long> FileIds
);
