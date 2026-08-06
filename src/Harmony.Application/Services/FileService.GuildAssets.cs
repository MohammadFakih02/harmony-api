using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Services;

// Guild assets (icon/banner) — same pipeline as user assets, guild-scoped; ManageGuild is enforced at
// the route, so ownership is not re-checked here.
public sealed partial class FileService
{
    public async Task<PresignFileResponse> PresignGuildAssetAsync(
        long actorId,
        long guildId,
        string kind,
        PresignFileRequest request,
        CancellationToken ct = default
    )
    {
        var prefix = GuildAssetPrefix(kind);

        if (!UserAssetContentTypes.Contains(request.ContentType))
            throw new ArgumentException("Icons and banners must be png, jpeg, gif, or webp.");

        if (request.SizeBytes <= 0 || request.SizeBytes > MaxUserAssetSizeBytes)
            throw new ArgumentException("File size is out of the allowed range.");

        if (await _guilds.GetByIdAsync(guildId) is null)
            throw new KeyNotFoundException("Guild not found.");

        var fileId = _snowflake.NextId();
        var objectKey = $"{prefix}/{guildId}/{fileId}";

        await _files.AddAsync(
            new FileAttachment
            {
                Id = fileId,
                UploaderId = actorId,
                GuildId = guildId,
                ChannelId = null, // guild assets have no channel container
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

    public async Task<UserAssetResponse> ConfirmGuildAssetAsync(
        long guildId,
        string kind,
        long fileId,
        CancellationToken ct = default
    )
    {
        var prefix = GuildAssetPrefix(kind);

        var file = await _files.GetByIdAsync(fileId);
        // The key must sit under THIS guild's asset prefix — a chat attachment, profile asset, or
        // another guild's asset can never be confirmed through this route. 404, don't leak.
        if (file is null || !file.MinioKey.StartsWith($"{prefix}/{guildId}/", StringComparison.Ordinal))
            throw new KeyNotFoundException("File not found.");

        await ValidateAndCapConfirmedImageAsync(
            file,
            kind == "icon" ? AvatarMaxDimension : BannerMaxDimension,
            "Icons and banners must be png, jpeg, gif, or webp.",
            ct
        );

        var guild = await _guilds.GetByIdAsync(guildId)
            ?? throw new KeyNotFoundException("Guild not found.");

        var oldKey = kind == "icon" ? guild.IconKey : guild.BannerKey;
        if (kind == "icon")
            guild.IconKey = file.MinioKey;
        else
            guild.BannerKey = file.MinioKey;

        await RetireReplacedAssetAsync(oldKey, file.MinioKey, prefix, ct);

        await _files.SaveChangesAsync();

        return new UserAssetResponse(file.MinioKey);
    }

    public async Task RemoveGuildAssetAsync(long guildId, string kind, CancellationToken ct = default)
    {
        var prefix = GuildAssetPrefix(kind);

        var guild = await _guilds.GetByIdAsync(guildId)
            ?? throw new KeyNotFoundException("Guild not found.");

        var key = kind == "icon" ? guild.IconKey : guild.BannerKey;
        if (key is null)
            return; // idempotent

        if (kind == "icon")
            guild.IconKey = null;
        else
            guild.BannerKey = null;

        await DeleteAssetAsync(key, prefix, ct);

        await _guilds.SaveChangesAsync();
    }

    private static string GuildAssetPrefix(string kind) =>
        kind switch
        {
            "icon" => "guild-icons",
            "banner" => "guild-banners",
            _ => throw new ArgumentException("Asset kind must be 'icon' or 'banner'."),
        };
}
