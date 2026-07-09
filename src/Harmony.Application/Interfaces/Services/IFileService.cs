using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// The file-upload use case: mint a presigned PUT for a pending attachment, then confirm it once
/// the client has uploaded directly to the object store. AttachFiles is enforced at the route by
/// the permission filter; ownership of a pending file is enforced here at confirm time.
/// </summary>
public interface IFileService
{
    /// <summary>
    /// Validates the request, records a pending <c>FileAttachment</c>, and returns a presigned PUT
    /// URL. Throws <see cref="KeyNotFoundException"/> if the channel is missing or not in the guild,
    /// <see cref="ArgumentException"/> if the type/size is rejected.
    /// </summary>
    Task<PresignFileResponse> PresignAsync(
        long userId,
        long? guildId,
        long channelId,
        PresignFileRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// Verifies the uploaded object actually exists, finalizes the row from the store's authoritative
    /// size/content-type (and image dimensions), and marks it confirmed. Idempotent. Throws
    /// <see cref="KeyNotFoundException"/> if unknown, <see cref="UnauthorizedAccessException"/> if the
    /// caller is not the uploader, <see cref="ArgumentException"/> if the object is missing or invalid.
    /// </summary>
    Task<FileAttachmentResponse> ConfirmAsync(
        long userId,
        long fileId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Mints a short-lived presigned GET URL for a confirmed file in the given channel. ViewChannel
    /// is enforced by the route filter; this verifies the file exists, is confirmed, and actually
    /// belongs to that channel (otherwise <see cref="KeyNotFoundException"/> — never leak existence
    /// or pending uploads, and keep files scoped to their channel).
    /// </summary>
    Task<FileDownloadResponse> GetDownloadUrlAsync(
        long? guildId,
        long channelId,
        long fileId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Presigns a profile-asset upload (<paramref name="kind"/> = "avatar" | "banner"). Image types
    /// only, tighter size cap than chat attachments, keyed under avatars/{userId}/… or
    /// banners/{userId}/…. Throws <see cref="ArgumentException"/> on a bad kind/type/size.
    /// </summary>
    Task<PresignFileResponse> PresignUserAssetAsync(
        long userId,
        string kind,
        PresignFileRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// Confirms an uploaded profile asset (store-as-truth + ImageSharp validation, like ConfirmAsync),
    /// then points the user's AvatarKey/BannerKey at it and best-effort deletes the replaced object.
    /// </summary>
    Task<UserAssetResponse> ConfirmUserAssetAsync(
        long userId,
        string kind,
        long fileId,
        CancellationToken ct = default
    );

    /// <summary>Clears the user's avatar/banner and best-effort deletes the stored object + row.</summary>
    Task RemoveUserAssetAsync(long userId, string kind, CancellationToken ct = default);

    /// <summary>
    /// Presigns a guild-asset upload (<paramref name="kind"/> = "icon" | "banner"). Same image-only
    /// rules and size cap as profile assets, keyed under guild-icons/{guildId}/… or
    /// guild-banners/{guildId}/…. ManageGuild is enforced at the route.
    /// </summary>
    Task<PresignFileResponse> PresignGuildAssetAsync(
        long actorId,
        long guildId,
        string kind,
        PresignFileRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// Confirms an uploaded guild asset, points the guild's IconKey/BannerKey at it, and
    /// best-effort deletes the replaced object + row.
    /// </summary>
    Task<UserAssetResponse> ConfirmGuildAssetAsync(
        long guildId,
        string kind,
        long fileId,
        CancellationToken ct = default
    );

    /// <summary>Clears the guild's icon/banner and best-effort deletes the stored object + row.</summary>
    Task RemoveGuildAssetAsync(long guildId, string kind, CancellationToken ct = default);

    /// <summary>
    /// Presigns a group-DM icon upload. Same image-only rules and size cap as profile assets,
    /// keyed under channel-icons/{channelId}/…. Participant + group-type gating is enforced at
    /// the route (the flat group model — any participant may change the icon).
    /// </summary>
    Task<PresignFileResponse> PresignGroupDmIconAsync(
        long actorId,
        long channelId,
        PresignFileRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// Confirms an uploaded group-DM icon, points the channel's IconKey at it, and best-effort
    /// deletes the replaced object + row.
    /// </summary>
    Task<UserAssetResponse> ConfirmGroupDmIconAsync(
        long channelId,
        long fileId,
        CancellationToken ct = default
    );

    /// <summary>Clears the group-DM icon and best-effort deletes the stored object + row.</summary>
    Task RemoveGroupDmIconAsync(long channelId, CancellationToken ct = default);

    /// <summary>
    /// Mints a presigned GET URL for a public asset. Only confirmed rows whose key sits under
    /// avatars/, banners/, guild-icons/, guild-banners/ or channel-icons/ resolve (chat
    /// attachments can never be served through this); anything else throws
    /// <see cref="KeyNotFoundException"/>.
    /// </summary>
    Task<string> GetPublicFileUrlAsync(string key, CancellationToken ct = default);
}
