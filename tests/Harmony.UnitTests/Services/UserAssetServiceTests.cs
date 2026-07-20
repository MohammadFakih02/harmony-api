using FluentAssertions;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Moq;

namespace Harmony.UnitTests.Services;

/// <summary>
/// The user-asset (avatar/banner) half of FileService — presign gates, confirm-sets-the-profile-key,
/// replacement cleanup, and the public-serve key checks. Storage and repos are mocked.
/// </summary>
public class UserAssetServiceTests
{
    private const long UserId = 100;
    private const long FileId = 400;

    private sealed record Sut(
        FileService Service,
        Mock<IFileAttachmentRepository> Files,
        Mock<IUserRepository> Users,
        Mock<IFileStorageService> Storage,
        Mock<IGuildRepository> Guilds,
        Mock<IHubBroadcaster> Broadcaster,
        User User
    );

    private static Sut BuildSut()
    {
        var files = new Mock<IFileAttachmentRepository>();
        var channels = new Mock<IChannelRepository>();
        var users = new Mock<IUserRepository>();
        var storage = new Mock<IFileStorageService>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();
        snowflake.Setup(s => s.NextId()).Returns(FileId);

        var user = new User { Id = UserId };
        users.Setup(u => u.GetByIdAsync(UserId)).ReturnsAsync(user);

        var guilds = new Mock<IGuildRepository>();
        guilds.Setup(g => g.GetGuildIdsForUserAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
        var friends = new Mock<IFriendRepository>();
        friends.Setup(f => f.GetFriendIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
        var broadcaster = new Mock<IHubBroadcaster>();

        storage
            .Setup(s => s.GetPresignedPutUrlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/presigned");
        storage
            .Setup(s => s.GetPresignedGetUrlAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/download");

        var service = new FileService(
            files.Object, channels.Object, users.Object, guilds.Object, friends.Object,
            storage.Object, snowflake.Object, broadcaster.Object);
        return new Sut(service, files, users, storage, guilds, broadcaster, user);
    }

    private static FileAttachment PendingAvatarRow(long id = FileId, long uploaderId = UserId) =>
        new()
        {
            Id = id,
            UploaderId = uploaderId,
            GuildId = null,
            ChannelId = null,
            MinioKey = $"avatars/{uploaderId}/{id}",
            Filename = "me.png",
            ContentType = "image/png",
            SizeBytes = 1024,
            IsConfirmed = false,
        };

    private static void SetupValidUpload(Sut sut, FileAttachment row)
    {
        sut.Files.Setup(f => f.GetByIdAsync(row.Id)).ReturnsAsync(row);
        sut.Storage
            .Setup(s => s.StatObjectAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "image/png"));
        sut.Storage
            .Setup(s => s.TryReadImageDimensionsAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((128, 128));
    }

    // ---- Presign ----------------------------------------------------------

    [Fact]
    public async Task Presign_HappyPath_KeysUnderAvatars_WithNoChannelContainer()
    {
        var sut = BuildSut();
        FileAttachment? added = null;
        sut.Files.Setup(f => f.AddAsync(It.IsAny<FileAttachment>()))
            .Callback<FileAttachment>(f => added = f)
            .Returns(Task.CompletedTask);

        var resp = await sut.Service.PresignUserAssetAsync(
            UserId, "avatar", new PresignFileRequest("me.png", "image/png", 1024));

        resp.ObjectKey.Should().Be($"avatars/{UserId}/{FileId}");
        added.Should().NotBeNull();
        added!.GuildId.Should().BeNull();
        added.ChannelId.Should().BeNull();
        added.IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Presign_RejectsNonImageType()
    {
        var sut = BuildSut();
        var act = () => sut.Service.PresignUserAssetAsync(
            UserId, "avatar", new PresignFileRequest("cv.pdf", "application/pdf", 1024));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Presign_RejectsOversizedAsset()
    {
        var sut = BuildSut();
        var act = () => sut.Service.PresignUserAssetAsync(
            UserId, "banner", new PresignFileRequest("big.png", "image/png",
                FileService.MaxUserAssetSizeBytes + 1));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Presign_RejectsUnknownKind()
    {
        var sut = BuildSut();
        var act = () => sut.Service.PresignUserAssetAsync(
            UserId, "wallpaper", new PresignFileRequest("x.png", "image/png", 1024));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- Confirm ----------------------------------------------------------

    [Fact]
    public async Task Confirm_SetsAvatarKeyOnTheUser()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        SetupValidUpload(sut, row);

        var resp = await sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);

        resp.Key.Should().Be(row.MinioKey);
        sut.User.AvatarKey.Should().Be(row.MinioKey);
        row.IsConfirmed.Should().BeTrue();
        row.Width.Should().Be(128);
        sut.Files.Verify(f => f.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Confirm_LargeAvatar_IsCappedInPlace_WithTheResultAsRowTruth()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        SetupValidUpload(sut, row);
        sut.Storage
            .Setup(s => s.TryReadImageDimensionsAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((2000, 1500));
        sut.Storage
            .Setup(s => s.DownscaleImageAsync(
                row.MinioKey, row.MinioKey,
                FileService.AvatarMaxDimension, FileService.AvatarMaxDimension,
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredImageResult(512, 384, 30_000, "image/png"));

        await sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);

        row.Width.Should().Be(512);
        row.Height.Should().Be(384);
        row.SizeBytes.Should().Be(30_000);
        row.IsConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task Confirm_Banner_UsesTheWiderCap()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        row.MinioKey = $"banners/{UserId}/{FileId}";
        SetupValidUpload(sut, row);
        sut.Storage
            .Setup(s => s.StatObjectAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "image/png"));
        sut.Storage
            .Setup(s => s.TryReadImageDimensionsAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((3840, 2160));

        await sut.Service.ConfirmUserAssetAsync(UserId, "banner", FileId);

        sut.Storage.Verify(s => s.DownscaleImageAsync(
            row.MinioKey, row.MinioKey,
            FileService.BannerMaxDimension, FileService.BannerMaxDimension,
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_Gif_IsNeverResized()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        SetupValidUpload(sut, row);
        sut.Storage
            .Setup(s => s.StatObjectAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "image/gif"));

        await sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);

        row.IsConfirmed.Should().BeTrue();
        sut.Storage.Verify(s => s.DownscaleImageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_DownscaleFailure_KeepsTheOriginalAndStillConfirms()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        SetupValidUpload(sut, row);
        sut.Storage
            .Setup(s => s.TryReadImageDimensionsAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((2000, 1500));
        // Loose-mock default: DownscaleImageAsync returns null = fail-open no-op.

        await sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);

        row.IsConfirmed.Should().BeTrue();
        row.Width.Should().Be(2000); // original, untouched
        row.SizeBytes.Should().Be(2048); // the stat value
    }

    [Fact]
    public async Task Confirm_Avatar_BroadcastsToGuildsAndSelf()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        SetupValidUpload(sut, row);
        sut.Guilds.Setup(g => g.GetGuildIdsForUserAsync(UserId)).ReturnsAsync(new List<long> { 7 });

        await sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);

        sut.Broadcaster.Verify(
            b => b.BroadcastProfileUpdatedToGuildAsync(
                7, It.Is<ProfileUpdatedPayload>(p => p.UserId == UserId && p.AvatarKey == row.MinioKey),
                It.IsAny<CancellationToken>()),
            Times.Once);
        sut.Broadcaster.Verify(
            b => b.BroadcastProfileUpdatedToUserAsync(
                UserId, It.IsAny<ProfileUpdatedPayload>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Confirm_Banner_DoesNotBroadcast()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        row.MinioKey = $"banners/{UserId}/{FileId}";
        SetupValidUpload(sut, row);

        await sut.Service.ConfirmUserAssetAsync(UserId, "banner", FileId);

        sut.Broadcaster.Verify(
            b => b.BroadcastProfileUpdatedToUserAsync(
                It.IsAny<long>(), It.IsAny<ProfileUpdatedPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Confirm_ReplacingAnAvatar_DeletesTheOldObjectAndRow()
    {
        var sut = BuildSut();
        var oldRow = PendingAvatarRow(id: 399);
        oldRow.IsConfirmed = true;
        sut.User.AvatarKey = oldRow.MinioKey;
        sut.Files.Setup(f => f.GetByIdAsync(oldRow.Id)).ReturnsAsync(oldRow);

        var row = PendingAvatarRow();
        SetupValidUpload(sut, row);

        await sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);

        sut.User.AvatarKey.Should().Be(row.MinioKey);
        sut.Storage.Verify(
            s => s.DeleteObjectAsync(oldRow.MinioKey, It.IsAny<CancellationToken>()), Times.Once);
        sut.Files.Verify(
            f => f.RemoveRange(It.Is<IEnumerable<FileAttachment>>(r => r.Contains(oldRow))),
            Times.Once);
    }

    [Fact]
    public async Task Confirm_RejectsAChatAttachmentKey()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        row.MinioKey = $"attachments/1/2/{FileId}"; // not a profile asset
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);

        var act = () => sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Confirm_RejectsSomeoneElsesUpload()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow(uploaderId: 999);
        row.MinioKey = $"avatars/999/{FileId}";
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);

        var act = () => sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Confirm_RejectsUndecodableImageBytes()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);
        sut.Storage
            .Setup(s => s.StatObjectAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "image/png"));
        sut.Storage
            .Setup(s => s.TryReadImageDimensionsAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValueTuple<int, int>?)null);

        var act = () => sut.Service.ConfirmUserAssetAsync(UserId, "avatar", FileId);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- Remove -----------------------------------------------------------

    [Fact]
    public async Task Remove_ClearsTheKeyAndDeletesTheObject()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        row.IsConfirmed = true;
        sut.User.AvatarKey = row.MinioKey;
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);

        await sut.Service.RemoveUserAssetAsync(UserId, "avatar");

        sut.User.AvatarKey.Should().BeNull();
        sut.Storage.Verify(
            s => s.DeleteObjectAsync(row.MinioKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Remove_WithNoAsset_IsAQuietNoOp()
    {
        var sut = BuildSut();
        await sut.Service.RemoveUserAssetAsync(UserId, "banner");
        sut.Files.Verify(f => f.SaveChangesAsync(), Times.Never);
    }

    // ---- Public serve -----------------------------------------------------

    [Fact]
    public async Task PublicUrl_ResolvesAConfirmedAsset()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow();
        row.IsConfirmed = true;
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);

        var url = await sut.Service.GetPublicFileUrlAsync(row.MinioKey);
        url.Should().Be("http://minio/download");
    }

    [Fact]
    public async Task PublicUrl_NeverServesChatAttachmentKeys()
    {
        var sut = BuildSut();
        var act = () => sut.Service.GetPublicFileUrlAsync($"attachments/1/2/{FileId}");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task PublicUrl_RejectsUnconfirmedOrMismatchedRows()
    {
        var sut = BuildSut();
        var row = PendingAvatarRow(); // IsConfirmed = false
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);

        var act = () => sut.Service.GetPublicFileUrlAsync(row.MinioKey);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
