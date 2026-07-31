using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Services;

// User profile assets (avatar/banner) — user-scoped presign + confirm; the caller confirms ownership
// (UploaderId), and only the avatar change is broadcast live.
public sealed partial class FileService
{
    public async Task<PresignFileResponse> PresignUserAssetAsync(
        long userId,
        string kind,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        var prefix = AssetPrefix(kind);

        if (!UserAssetContentTypes.Contains(request.ContentType))
            throw new ArgumentException("Avatars and banners must be png, jpeg, gif, or webp.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxUserAssetSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        var fileId = _snowflake.NextId();
        var objectKey = $"{prefix}/{userId}/{fileId}";

        await _files.AddAsync(
            new FileAttachment
            {
                Id = fileId,
                UploaderId = userId,
                GuildId = null,
                ChannelId = null, // profile assets have no channel container
                MinioKey = objectKey,
                Filename = request.Filename,
                ContentType = request.ContentType,
                SizeBytes = request.SizeBytes,
                IsConfirmed = false,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await _files.SaveChangesAsync();

        var url = await _storage.GetPresignedPutUrlAsync(
            objectKey,
            request.ContentType,
            PresignExpiry,
            ct
        );

        var expiresAt = DateTimeOffset.UtcNow.Add(PresignExpiry).ToUnixTimeMilliseconds();
        return new PresignFileResponse(fileId, url, objectKey, expiresAt);
    }

    public async Task<UserAssetResponse> ConfirmUserAssetAsync(
        long userId,
        string kind,
        long fileId,
        CancellationToken ct = default
    )
    {
        var prefix = AssetPrefix(kind);

        var file = await _files.GetByIdAsync(fileId);
        // A chat attachment (or the other asset kind) can never be confirmed through this path —
        // 404 like any unknown id, don't leak that the row exists.
        if (file is null || !file.MinioKey.StartsWith($"{prefix}/", StringComparison.Ordinal))
            throw new KeyNotFoundException("File not found.");

        if (file.UploaderId != userId)
            throw new UnauthorizedAccessException("You did not upload this file.");

        await ValidateAndCapConfirmedImageAsync(
            file,
            kind == "avatar" ? AvatarMaxDimension : BannerMaxDimension,
            "Avatars and banners must be png, jpeg, gif, or webp.",
            ct
        );

        var user = await _users.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        var oldKey = kind == "avatar" ? user.AvatarKey : user.BannerKey;
        if (kind == "avatar")
            user.AvatarKey = file.MinioKey;
        else
            user.BannerKey = file.MinioKey;

        // Retire the replaced asset (object + row), best-effort — same posture as the orphan
        // sweep: an object-delete failure never fails the confirm, the row is reclaimed anyway.
        await RetireReplacedAssetAsync(oldKey, file.MinioKey, prefix, ct);

        // Repos share the scoped DbContext — one save commits the file + user + row removal.
        await _files.SaveChangesAsync();

        // Live-push the new avatar so member lists / chat / DMs update without a refetch (banner
        // isn't rendered anywhere that live-updates, so only avatar changes are broadcast).
        if (kind == "avatar")
            await BroadcastAvatarUpdatedAsync(userId, file.MinioKey, ct);

        return new UserAssetResponse(file.MinioKey);
    }

    public async Task RemoveUserAssetAsync(long userId, string kind, CancellationToken ct = default)
    {
        var prefix = AssetPrefix(kind);

        var user = await _users.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        var key = kind == "avatar" ? user.AvatarKey : user.BannerKey;
        if (key is null)
            return; // idempotent — nothing to remove

        if (kind == "avatar")
            user.AvatarKey = null;
        else
            user.BannerKey = null;

        await DeleteAssetAsync(key, prefix, ct);

        await _files.SaveChangesAsync();

        if (kind == "avatar")
            await BroadcastAvatarUpdatedAsync(userId, null, ct);
    }

    /// <summary>
    /// Fans an avatar change out to the surfaces that render it live: every guild the user belongs
    /// to (guild groups), their friends, and their own tabs. Best-effort — a broadcast failure must
    /// never fail the upload/removal it follows (the change is already committed).
    /// </summary>
    private async Task BroadcastAvatarUpdatedAsync(long userId, string? avatarKey, CancellationToken ct)
    {
        try
        {
            var payload = new ProfileUpdatedPayload(userId, avatarKey, Username: null);

            var guildIds = await _guilds.GetGuildIdsForUserAsync(userId);
            foreach (var guildId in guildIds)
                await _broadcaster.BroadcastProfileUpdatedToGuildAsync(guildId, payload, ct);

            // The user's own tabs (deck avatar) + each friend (DM list / friends list).
            await _broadcaster.BroadcastProfileUpdatedToUserAsync(userId, payload, ct);
            var friendIds = await _friends.GetFriendIdsAsync(userId);
            foreach (var friendId in friendIds)
                await _broadcaster.BroadcastProfileUpdatedToUserAsync(friendId, payload, ct);
        }
        catch
        {
            // best-effort — see summary
        }
    }

    private static string AssetPrefix(string kind) =>
        kind switch
        {
            "avatar" => "avatars",
            "banner" => "banners",
            _ => throw new ArgumentException("Asset kind must be 'avatar' or 'banner'."),
        };
}
