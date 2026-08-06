using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Services;

// Shared pipeline for the three image-asset flows (user avatar/banner, guild icon/banner, group-DM
// icon). Each flow's Presign/Confirm/Remove lives in its own partial file and differs only in the
// owner entity, key layout, and error wording; the byte-identical middle steps are factored here so a
// change to "how an asset is validated/capped/retired" is a one-place edit that every flow inherits.
public sealed partial class FileService
{
    /// <summary>
    /// The shared confirm step: when the row isn't already confirmed, verifies the uploaded object's
    /// authoritative size and content-type, that it decodes as an image (recording its dimensions),
    /// caps it in place to <paramref name="maxDimension"/>, then flips <c>IsConfirmed</c>. Idempotent —
    /// a no-op if already confirmed. <paramref name="typeError"/> is the (per-flow) wording surfaced
    /// when the content type isn't an allowed image ("Avatars and banners…", "Icons…", etc.).
    /// </summary>
    private async Task ValidateAndCapConfirmedImageAsync(
        FileAttachment file,
        int maxDimension,
        string typeError,
        CancellationToken ct
    )
    {
        if (file.IsConfirmed)
            return;

        var stat = await _storage.StatObjectAsync(file.MinioKey, ct);
        if (stat is null)
            throw new ArgumentException("Uploaded object was not found in storage.");

        if (stat.Size <= 0 || stat.Size > MaxUserAssetSizeBytes)
            throw new ArgumentException("Uploaded object exceeds the maximum allowed size.");

        file.SizeBytes = stat.Size;
        file.ContentType = stat.ContentType;

        if (!UserAssetContentTypes.Contains(file.ContentType))
            throw new ArgumentException(typeError);

        // Profile/guild/group-DM assets are always images — a successful decode IS the byte validation.
        var dims = await _storage.TryReadImageDimensionsAsync(file.MinioKey, ct);
        if (dims is not { } d)
            throw new ArgumentException("Uploaded object is not a valid image.");

        file.Width = d.Width;
        file.Height = d.Height;

        await CapAssetInPlaceAsync(file, maxDimension, ct);

        file.IsConfirmed = true;
    }

    /// <summary>
    /// Retires the asset a confirm just replaced: best-effort deletes the old object and removes its
    /// row, but only when <paramref name="oldKey"/> is a real, different key under this flow's
    /// <paramref name="prefix"/>. Does NOT save — the caller's single <c>SaveChangesAsync</c> commits
    /// the removal alongside the owner update.
    /// </summary>
    private async Task RetireReplacedAssetAsync(
        string? oldKey,
        string newKey,
        string prefix,
        CancellationToken ct
    )
    {
        if (oldKey is null || oldKey == newKey)
            return;

        await DeleteAssetAsync(oldKey, prefix, ct);
    }

    /// <summary>
    /// Best-effort delete of an asset object + its row, guarded to this flow's <paramref name="prefix"/>
    /// (a key outside it is left untouched). An object-delete failure never propagates — the row is
    /// reclaimed regardless, same posture as the orphan sweep. Does NOT save — the caller commits.
    /// </summary>
    private async Task DeleteAssetAsync(string key, string prefix, CancellationToken ct)
    {
        if (!key.StartsWith($"{prefix}/", StringComparison.Ordinal))
            return;

        try
        {
            await _storage.DeleteObjectAsync(key, ct);
        }
        catch
        {
            // best-effort — a stale object in the store is harmless
        }

        if (TryParseAssetFileId(key, out var fileId)
            && await _files.GetByIdAsync(fileId) is { } row)
        {
            _files.RemoveRange([row]);
        }
    }
}
