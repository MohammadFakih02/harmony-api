namespace Harmony.Application.Services;

/// <summary>
/// Per-type magic-byte sniffing for the attachment allowlist. Pure (no IO/SDK) so it is
/// unit-testable with raw byte arrays. Used by <see cref="FileService"/> at confirm to verify the
/// object's actual leading bytes are consistent with its declared content type — the non-image
/// counterpart to ImageSharp's decode check (which images keep, because it also yields dimensions).
///
/// Signature-less text types are accepted on type alone: there is no reliable header for plain text,
/// and text is only ever served back as a download (never executed), so there is nothing to spoof
/// into. Anything not on the allowlist returns false.
/// </summary>
public static class FileSignatures
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    };

    /// <summary>True for the image types whose validation goes through ImageSharp (decode + dims).</summary>
    public static bool IsImage(string contentType) => ImageTypes.Contains(contentType);

    /// <summary>
    /// Verifies the object's leading bytes match the declared non-image content type. Image types
    /// are not handled here (the caller validates those by decoding); calling with an image type
    /// returns false. Text types have no signature and are accepted.
    /// </summary>
    public static bool IsConsistent(string contentType, ReadOnlySpan<byte> head) =>
        contentType.ToLowerInvariant() switch
        {
            "application/pdf" => StartsWith(head, "%PDF"u8),

            // ISO base media (MP4 / QuickTime): the "ftyp" box tag sits at offset 4.
            "video/mp4" or "video/quicktime" => HasAt(head, 4, "ftyp"u8),

            // Matroska / WebM (both video and audio variants) share the EBML header.
            "video/webm" or "audio/webm" => StartsWith(head, stackalloc byte[] { 0x1A, 0x45, 0xDF, 0xA3 }),

            // MP3: an ID3 tag, or a raw MPEG audio frame sync (11 set bits).
            "audio/mpeg" => StartsWith(head, "ID3"u8)
                || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0),

            "audio/ogg" => StartsWith(head, "OggS"u8),

            // WAV: RIFF container with a WAVE form type at offset 8.
            "audio/wav" => StartsWith(head, "RIFF"u8) && HasAt(head, 8, "WAVE"u8),

            // ZIP local-file (03 04), empty-archive (05 06), or spanned (07 08) signature.
            "application/zip" => head.Length >= 4
                && head[0] == 0x50 && head[1] == 0x4B
                && ((head[2] == 0x03 && head[3] == 0x04)
                    || (head[2] == 0x05 && head[3] == 0x06)
                    || (head[2] == 0x07 && head[3] == 0x08)),

            // Signature-less, accepted on type alone.
            "text/plain" or "text/csv" or "text/markdown" => true,

            _ => false,
        };

    private static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> sig) =>
        head.Length >= sig.Length && head[..sig.Length].SequenceEqual(sig);

    private static bool HasAt(ReadOnlySpan<byte> head, int offset, ReadOnlySpan<byte> sig) =>
        head.Length >= offset + sig.Length && head.Slice(offset, sig.Length).SequenceEqual(sig);
}
