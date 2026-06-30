using FluentAssertions;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Moq;

namespace Harmony.UnitTests.Services;

/// <summary>
/// FileService unit tests — storage and repo are mocked, so no MinIO is needed. Covers the presign
/// validation gates and the confirm path (ownership, idempotency, store-as-truth, magic-byte check).
/// </summary>
public class FileServiceTests
{
    private const long UserId = 100;
    private const long GuildId = 200;
    private const long ChannelId = 300;
    private const long FileId = 400;

    private static (
        FileService sut,
        Mock<IFileAttachmentRepository> files,
        Mock<IChannelRepository> channels,
        Mock<IFileStorageService> storage
    ) BuildSut()
    {
        var files = new Mock<IFileAttachmentRepository>();
        var channels = new Mock<IChannelRepository>();
        var storage = new Mock<IFileStorageService>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();
        snowflake.Setup(s => s.NextId()).Returns(FileId);

        // Channel exists and belongs to the guild by default.
        channels
            .Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId))
            .ReturnsAsync(new Channel { Id = ChannelId, GuildId = GuildId });

        storage
            .Setup(s => s.GetPresignedPutUrlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/presigned");

        storage
            .Setup(s => s.GetPresignedGetUrlAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/download");

        var sut = new FileService(files.Object, channels.Object, storage.Object, snowflake.Object);
        return (sut, files, channels, storage);
    }

    private static PresignFileRequest ValidRequest(long size = 1024) =>
        new("photo.png", "image/png", size);

    // ---- Presign ----------------------------------------------------------

    [Fact]
    public async Task Presign_HappyPath_AddsPendingRowAndReturnsUrl()
    {
        var (sut, files, _, _) = BuildSut();
        FileAttachment? added = null;
        files.Setup(f => f.AddAsync(It.IsAny<FileAttachment>()))
            .Callback<FileAttachment>(f => added = f)
            .Returns(Task.CompletedTask);

        var result = await sut.PresignAsync(UserId, GuildId, ChannelId, ValidRequest());

        result.FileId.Should().Be(FileId);
        result.UploadUrl.Should().Be("http://minio/presigned");
        added.Should().NotBeNull();
        added!.IsConfirmed.Should().BeFalse();
        added.UploaderId.Should().Be(UserId);
        added.MinioKey.Should().Be($"attachments/{GuildId}/{ChannelId}/{FileId}");
        files.Verify(f => f.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Presign_UnknownChannel_Throws404()
    {
        var (sut, _, channels, _) = BuildSut();
        channels.Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId)).ReturnsAsync((Channel?)null);

        await sut.Invoking(s => s.PresignAsync(UserId, GuildId, ChannelId, ValidRequest()))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Presign_DisallowedContentType_ThrowsArgument()
    {
        var (sut, _, _, _) = BuildSut();
        var req = new PresignFileRequest("evil.exe", "application/x-msdownload", 1024);

        await sut.Invoking(s => s.PresignAsync(UserId, GuildId, ChannelId, req))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Presign_Oversize_ThrowsArgument()
    {
        var (sut, _, _, _) = BuildSut();
        var req = ValidRequest(FileService.MaxFileSizeBytes + 1);

        await sut.Invoking(s => s.PresignAsync(UserId, GuildId, ChannelId, req))
            .Should().ThrowAsync<ArgumentException>();
    }

    // ---- Confirm ----------------------------------------------------------

    private static FileAttachment PendingFile() =>
        new()
        {
            Id = FileId,
            UploaderId = UserId,
            GuildId = GuildId,
            ChannelId = ChannelId,
            MinioKey = $"attachments/{GuildId}/{ChannelId}/{FileId}",
            Filename = "photo.png",
            ContentType = "image/png",
            SizeBytes = 1024,
            IsConfirmed = false,
        };

    [Fact]
    public async Task Confirm_HappyPath_SetsConfirmedWithStoreTruthAndDimensions()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        // Store reports a different (authoritative) size + type than the client declared.
        storage.Setup(s => s.StatObjectAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "image/png"));
        storage.Setup(s => s.TryReadImageDimensionsAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((64, 48));

        var result = await sut.ConfirmAsync(UserId, FileId);

        result.IsConfirmed.Should().BeTrue();
        result.SizeBytes.Should().Be(2048);
        result.Width.Should().Be(64);
        result.Height.Should().Be(48);
        file.IsConfirmed.Should().BeTrue();
        files.Verify(f => f.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Confirm_NonOwner_Throws403()
    {
        var (sut, files, _, _) = BuildSut();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(PendingFile());

        await sut.Invoking(s => s.ConfirmAsync(UserId + 1, FileId))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Confirm_UnknownFile_Throws404()
    {
        var (sut, files, _, _) = BuildSut();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync((FileAttachment?)null);

        await sut.Invoking(s => s.ConfirmAsync(UserId, FileId))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Confirm_AlreadyConfirmed_IsIdempotent()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        file.IsConfirmed = true;
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);

        var result = await sut.ConfirmAsync(UserId, FileId);

        result.IsConfirmed.Should().BeTrue();
        storage.Verify(s => s.StatObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        files.Verify(f => f.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task Confirm_ObjectMissingInStore_ThrowsArgument()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        storage.Setup(s => s.StatObjectAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StoredObjectInfo?)null);

        await sut.Invoking(s => s.ConfirmAsync(UserId, FileId))
            .Should().ThrowAsync<ArgumentException>();
        file.IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_DeclaredImageThatDoesNotDecode_ThrowsArgument()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        storage.Setup(s => s.StatObjectAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(1024, "image/png"));
        // Not a real image → magic-byte mismatch.
        storage.Setup(s => s.TryReadImageDimensionsAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((int, int)?)null);

        await sut.Invoking(s => s.ConfirmAsync(UserId, FileId))
            .Should().ThrowAsync<ArgumentException>();
        file.IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_NonImage_WithMatchingSignature_SucceedsWithNullDimensions()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        file.Filename = "doc.pdf";
        file.ContentType = "application/pdf";
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        storage.Setup(s => s.StatObjectAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "application/pdf"));
        storage.Setup(s => s.ReadObjectHeadAsync(file.MinioKey, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("%PDF-1.7"u8.ToArray());

        var result = await sut.ConfirmAsync(UserId, FileId);

        result.IsConfirmed.Should().BeTrue();
        result.ContentType.Should().Be("application/pdf");
        result.Width.Should().BeNull();
        result.Height.Should().BeNull();
        // Non-image path must not run the image decode.
        storage.Verify(
            s => s.TryReadImageDimensionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Confirm_NonImage_WithMismatchedBytes_ThrowsArgument()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        file.ContentType = "application/pdf";
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        storage.Setup(s => s.StatObjectAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "application/pdf"));
        // The declared type is allowed, but the bytes are not a PDF.
        storage.Setup(s => s.ReadObjectHeadAsync(file.MinioKey, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("this is not a pdf"u8.ToArray());

        await sut.Invoking(s => s.ConfirmAsync(UserId, FileId))
            .Should().ThrowAsync<ArgumentException>();
        file.IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_StoreReportsDisallowedType_ThrowsArgument()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        // Authoritative store type is not on the allowlist (defense-in-depth re-check).
        storage.Setup(s => s.StatObjectAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, "application/x-msdownload"));

        await sut.Invoking(s => s.ConfirmAsync(UserId, FileId))
            .Should().ThrowAsync<ArgumentException>();
        file.IsConfirmed.Should().BeFalse();
    }

    // ---- Download ---------------------------------------------------------

    private static FileAttachment ConfirmedFile()
    {
        var f = PendingFile();
        f.IsConfirmed = true;
        return f;
    }

    [Fact]
    public async Task GetDownloadUrl_HappyPath_ReturnsUrl()
    {
        var (sut, files, _, _) = BuildSut();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(ConfirmedFile());

        var result = await sut.GetDownloadUrlAsync(GuildId, ChannelId, FileId);

        result.Url.Should().Be("http://minio/download");
        result.ExpiresAt.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetDownloadUrl_UnknownFile_Throws404()
    {
        var (sut, files, _, _) = BuildSut();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync((FileAttachment?)null);

        await sut.Invoking(s => s.GetDownloadUrlAsync(GuildId, ChannelId, FileId))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetDownloadUrl_UnconfirmedFile_Throws404()
    {
        var (sut, files, _, _) = BuildSut();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(PendingFile()); // IsConfirmed = false

        await sut.Invoking(s => s.GetDownloadUrlAsync(GuildId, ChannelId, FileId))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetDownloadUrl_FileInDifferentChannel_Throws404()
    {
        var (sut, files, _, _) = BuildSut();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(ConfirmedFile());

        // Confirmed file, but requested via a channel it doesn't belong to.
        await sut.Invoking(s => s.GetDownloadUrlAsync(GuildId, ChannelId + 1, FileId))
            .Should().ThrowAsync<KeyNotFoundException>();
    }
}
