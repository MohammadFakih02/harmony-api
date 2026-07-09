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
/// The group-DM icon half of FileService — presign gates (image-only, group_dm-only),
/// confirm-sets-the-channel-key with prefix scoping, replacement cleanup, and idempotent
/// remove. Storage and repos are mocked; participant gating lives in the controller.
/// </summary>
public class GroupDmIconServiceTests
{
    private const long ActorId = 100;
    private const long ChannelId = 300;
    private const long FileId = 400;

    private sealed record Sut(
        FileService Service,
        Mock<IFileAttachmentRepository> Files,
        Mock<IChannelRepository> Channels,
        Mock<IFileStorageService> Storage,
        Channel Channel
    );

    private static Sut BuildSut(string channelType = "group_dm")
    {
        var files = new Mock<IFileAttachmentRepository>();
        var channels = new Mock<IChannelRepository>();
        var users = new Mock<IUserRepository>();
        var storage = new Mock<IFileStorageService>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();
        snowflake.Setup(s => s.NextId()).Returns(FileId);

        var channel = new Channel { Id = ChannelId, Type = channelType, Name = "Group" };
        channels.Setup(c => c.GetByIdAsync(ChannelId)).ReturnsAsync(channel);

        var guilds = new Mock<IGuildRepository>();
        guilds.Setup(g => g.GetGuildIdsForUserAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
        var friends = new Mock<IFriendRepository>();
        friends.Setup(f => f.GetFriendIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
        var broadcaster = new Mock<IHubBroadcaster>();

        storage
            .Setup(s => s.GetPresignedPutUrlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/presigned");

        var service = new FileService(
            files.Object, channels.Object, users.Object, guilds.Object, friends.Object,
            storage.Object, snowflake.Object, broadcaster.Object);
        return new Sut(service, files, channels, storage, channel);
    }

    private static FileAttachment PendingIconRow(long id = FileId, long channelId = ChannelId) =>
        new()
        {
            Id = id,
            UploaderId = ActorId,
            GuildId = null,
            ChannelId = null,
            MinioKey = $"channel-icons/{channelId}/{id}",
            Filename = "icon.png",
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
            .ReturnsAsync((64, 64));
    }

    // ---- Presign ----------------------------------------------------------

    [Fact]
    public async Task Presign_HappyPath_KeysUnderChannelIcons_WithNoChannelContainer()
    {
        var sut = BuildSut();
        FileAttachment? added = null;
        sut.Files.Setup(f => f.AddAsync(It.IsAny<FileAttachment>()))
            .Callback<FileAttachment>(f => added = f)
            .Returns(Task.CompletedTask);

        var resp = await sut.Service.PresignGroupDmIconAsync(
            ActorId, ChannelId, new PresignFileRequest("icon.png", "image/png", 1024));

        resp.ObjectKey.Should().Be($"channel-icons/{ChannelId}/{FileId}");
        added.Should().NotBeNull();
        added!.GuildId.Should().BeNull();
        // A null ChannelId keeps the row un-attachable through the message send path.
        added.ChannelId.Should().BeNull();
        added.IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Presign_RejectsNonImageType()
    {
        var sut = BuildSut();
        var act = () => sut.Service.PresignGroupDmIconAsync(
            ActorId, ChannelId, new PresignFileRequest("clip.mp4", "video/mp4", 1024));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Presign_RejectsANonGroupChannel()
    {
        var sut = BuildSut(channelType: "dm");
        var act = () => sut.Service.PresignGroupDmIconAsync(
            ActorId, ChannelId, new PresignFileRequest("icon.png", "image/png", 1024));
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ---- Confirm ----------------------------------------------------------

    [Fact]
    public async Task Confirm_SetsIconKeyOnTheChannel()
    {
        var sut = BuildSut();
        var row = PendingIconRow();
        SetupValidUpload(sut, row);

        var resp = await sut.Service.ConfirmGroupDmIconAsync(ChannelId, FileId);

        resp.Key.Should().Be(row.MinioKey);
        sut.Channel.IconKey.Should().Be(row.MinioKey);
        row.IsConfirmed.Should().BeTrue();
        sut.Files.Verify(f => f.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Confirm_RejectsAKeyOutsideThisChannelsPrefix()
    {
        var sut = BuildSut();
        var row = PendingIconRow(channelId: 999); // another channel's icon
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);

        var act = () => sut.Service.ConfirmGroupDmIconAsync(ChannelId, FileId);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Confirm_RejectsUndecodableImageBytes()
    {
        var sut = BuildSut();
        var row = PendingIconRow();
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);
        sut.Storage
            .Setup(s => s.StatObjectAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "image/png"));
        sut.Storage
            .Setup(s => s.TryReadImageDimensionsAsync(row.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValueTuple<int, int>?)null);

        var act = () => sut.Service.ConfirmGroupDmIconAsync(ChannelId, FileId);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Confirm_ReplacingAnIcon_DeletesTheOldObjectAndRow()
    {
        var sut = BuildSut();
        var oldRow = PendingIconRow(id: 399);
        oldRow.IsConfirmed = true;
        sut.Channel.IconKey = oldRow.MinioKey;
        sut.Files.Setup(f => f.GetByIdAsync(oldRow.Id)).ReturnsAsync(oldRow);

        var row = PendingIconRow();
        SetupValidUpload(sut, row);

        await sut.Service.ConfirmGroupDmIconAsync(ChannelId, FileId);

        sut.Channel.IconKey.Should().Be(row.MinioKey);
        sut.Storage.Verify(
            s => s.DeleteObjectAsync(oldRow.MinioKey, It.IsAny<CancellationToken>()), Times.Once);
        sut.Files.Verify(
            f => f.RemoveRange(It.Is<IEnumerable<FileAttachment>>(r => r.Contains(oldRow))),
            Times.Once);
    }

    // ---- Remove -----------------------------------------------------------

    [Fact]
    public async Task Remove_ClearsTheKeyAndDeletesTheObject()
    {
        var sut = BuildSut();
        var row = PendingIconRow();
        row.IsConfirmed = true;
        sut.Channel.IconKey = row.MinioKey;
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);

        await sut.Service.RemoveGroupDmIconAsync(ChannelId);

        sut.Channel.IconKey.Should().BeNull();
        sut.Storage.Verify(
            s => s.DeleteObjectAsync(row.MinioKey, It.IsAny<CancellationToken>()), Times.Once);
        sut.Files.Verify(f => f.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Remove_WithNoIcon_IsAQuietNoOp()
    {
        var sut = BuildSut();
        await sut.Service.RemoveGroupDmIconAsync(ChannelId);
        sut.Files.Verify(f => f.SaveChangesAsync(), Times.Never);
    }

    // ---- Public serve -----------------------------------------------------

    [Fact]
    public async Task PublicUrl_ResolvesAConfirmedChannelIcon()
    {
        var sut = BuildSut();
        var row = PendingIconRow();
        row.IsConfirmed = true;
        sut.Files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(row);
        sut.Storage
            .Setup(s => s.GetPresignedGetUrlAsync(
                row.MinioKey, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/download");

        var url = await sut.Service.GetPublicFileUrlAsync(row.MinioKey);
        url.Should().Be("http://minio/download");
    }
}
