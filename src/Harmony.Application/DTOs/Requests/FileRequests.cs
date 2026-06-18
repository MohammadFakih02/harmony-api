namespace Harmony.Application.DTOs.Requests;

/// <summary>
/// Client-declared metadata for an upload. The size/type are validated up front to mint the
/// presigned URL, but are re-verified against the object store's authoritative values at confirm —
/// the client's claims are never trusted as final.
/// </summary>
public record PresignFileRequest(string Filename, string ContentType, long SizeBytes);
