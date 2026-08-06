using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Services;

// Group-DM icon — same pipeline as user/guild assets, channel-scoped; participant access is enforced
// at the route. A single icon per channel (no icon/banner kind), so the prefix is a fixed constant.
public sealed partial class FileService
{
    private const string ChannelIconPrefix = "channel-icons";

    public async Task<PresignFileResponse> PresignGroupDmIconAsync(
        long actorId,
        long channelId,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        if (!UserAssetContentTypes.Contains(request.ContentType))
            throw new ArgumentException("Icons must be png, jpeg, gif, or webp.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxUserAssetSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.Type != "group_dm")
            throw new KeyNotFoundException("Channel not found.");

        var fileId = _snowflake.NextId();
        var objectKey = $"{ChannelIconPrefix}/{channelId}/{fileId}";

        await _files.AddAsync(
            new FileAttachment
            {
                Id = fileId,
                UploaderId = actorId,
                // Both null: asset rows are identified by key prefix only, and a null ChannelId
                // keeps the row un-attachable through the message send path.
                GuildId = null,
                ChannelId = null,
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

    public async Task<UserAssetResponse> ConfirmGroupDmIconAsync(
        long channelId,
        long fileId,
        CancellationToken ct = default
    )
    {
        var file = await _files.GetByIdAsync(fileId);
        // The key must sit under THIS channel's icon prefix — a chat attachment, profile asset,
        // or another channel's icon can never be confirmed through this route. 404, don't leak.
        if (file is null
            || !file.MinioKey.StartsWith($"{ChannelIconPrefix}/{channelId}/", StringComparison.Ordinal))
            throw new KeyNotFoundException("File not found.");

        await ValidateAndCapConfirmedImageAsync(
            file,
            AvatarMaxDimension,
            "Icons must be png, jpeg, gif, or webp.",
            ct
        );

        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.Type != "group_dm")
            throw new KeyNotFoundException("Channel not found.");

        var oldKey = channel.IconKey;
        channel.IconKey = file.MinioKey;

        await RetireReplacedAssetAsync(oldKey, file.MinioKey, ChannelIconPrefix, ct);

        await _files.SaveChangesAsync();

        return new UserAssetResponse(file.MinioKey);
    }

    public async Task RemoveGroupDmIconAsync(long channelId, CancellationToken ct = default)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.Type != "group_dm")
            throw new KeyNotFoundException("Channel not found.");

        var key = channel.IconKey;
        if (key is null)
            return; // idempotent

        channel.IconKey = null;

        await DeleteAssetAsync(key, ChannelIconPrefix, ct);

        await _files.SaveChangesAsync();
    }
}
