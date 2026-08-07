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

        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetByIdAsync(UserId)).ReturnsAsync(new User { Id = UserId });

        var guilds = new Mock<IGuildRepository>();
        guilds.Setup(g => g.GetGuildIdsForUserAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
        var friends = new Mock<IFriendRepository>();
        friends.Setup(f => f.GetFriendIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());

        var sut = new FileService(
            files.Object, channels.Object, users.Object, guilds.Object, friends.Object,
            storage.Object, snowflake.Object, Mock.Of<IHubBroadcaster>(), Mock.Of<IUserDisplayCache>());
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
        storage.Setup(s => s.ProcessChatImageAsync(file.MinioKey, It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessedImage(64, 48, "LKO2:N%2Tw=w]~RBVZRi", new List<int>()));

        var result = await sut.ConfirmAsync(UserId, FileId);

        result.IsConfirmed.Should().BeTrue();
        result.SizeBytes.Should().Be(2048);
        result.Width.Should().Be(64);
        result.Height.Should().Be(48);
        // Confirm returns metadata only; the BlurHash is persisted on the row and surfaces on the
        // download DTO (asserted in GetDownloadUrl_BuildsASrcsetFromTheStoredVariantWidths).
        file.BlurHash.Should().Be("LKO2:N%2Tw=w]~RBVZRi");
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
        // Not a real image → magic-byte mismatch (ProcessChatImageAsync returns null on decode failure).
        storage.Setup(s => s.ProcessChatImageAsync(file.MinioKey, It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessedImage?)null);

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
        // Non-image path must not run the image processing pass.
        storage.Verify(
            s => s.ProcessChatImageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()),
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

    // ---- Chat image variants (A6) -----------------------------------------

    // Sets up a confirmable PNG whose ProcessChatImageAsync returns the given generated widths.
    private static void SetupImageConfirm(
        Mock<IFileAttachmentRepository> files,
        Mock<IFileStorageService> storage,
        FileAttachment file,
        int width,
        int height,
        IReadOnlyList<int> generatedWidths,
        string? blurHash = "LKO2:N%2Tw=w]~RBVZRi")
    {
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        storage.Setup(s => s.StatObjectAsync(file.MinioKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectInfo(2048, file.ContentType));
        storage.Setup(s => s.ProcessChatImageAsync(file.MinioKey, It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessedImage(width, height, blurHash, generatedWidths));
    }

    [Fact]
    public async Task Confirm_LargeImage_RecordsVariantsAndTheSmallestAsTheFallbackThumbnail()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        SetupImageConfirm(files, storage, file, 1600, 1200, new List<int> { 400, 800, 1200 });

        await sut.ConfirmAsync(UserId, FileId);

        file.VariantWidths.Should().Be("400,800,1200");
        // The smallest generated variant doubles as the lightweight non-srcset fallback thumbnail.
        file.ThumbnailKey.Should().Be($"{file.MinioKey}_w400");
        // The row keeps the store-authoritative size and the decoded dimensions.
        file.Width.Should().Be(1600);
        file.SizeBytes.Should().Be(2048);
    }

    [Fact]
    public async Task Confirm_SmallImage_RecordsNoVariantsAndNoThumbnail()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        // A 300px-wide image is below every variant width → the storage layer generates none.
        SetupImageConfirm(files, storage, file, 300, 200, new List<int>());

        await sut.ConfirmAsync(UserId, FileId);

        file.VariantWidths.Should().BeNull();
        file.ThumbnailKey.Should().BeNull();
    }

    [Fact]
    public async Task Confirm_Gif_RecordsNoVariants_ButStillGetsABlurHash()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        file.ContentType = "image/gif";
        // Animated → the storage layer returns dims + a first-frame blurhash but no variants.
        SetupImageConfirm(files, storage, file, 1920, 1080, new List<int>());

        await sut.ConfirmAsync(UserId, FileId);

        file.VariantWidths.Should().BeNull();
        file.ThumbnailKey.Should().BeNull();
        file.BlurHash.Should().NotBeNull();
    }

    [Fact]
    public async Task Confirm_VariantGenerationFailure_StillConfirms_WithNoVariants()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = PendingFile();
        // ProcessChatImageAsync is fail-open internally: a valid image still returns dims, just
        // with an empty variant list and possibly a null hash.
        SetupImageConfirm(files, storage, file, 1600, 1200, new List<int>(), blurHash: null);

        var result = await sut.ConfirmAsync(UserId, FileId);

        result.IsConfirmed.Should().BeTrue();
        file.ThumbnailKey.Should().BeNull();
        file.VariantWidths.Should().BeNull();
    }

    [Fact]
    public async Task GetDownloadUrl_BuildsASrcsetFromTheStoredVariantWidths()
    {
        var (sut, files, _, storage) = BuildSut();
        var file = ConfirmedFile();
        file.VariantWidths = "400,800";
        file.BlurHash = "LKO2:N%2Tw=w]~RBVZRi";
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(file);
        // Distinct URLs per key so we can assert the srcset maps width → the right variant key.
        storage.Setup(s => s.GetPresignedGetUrlAsync($"{file.MinioKey}_w400", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/w400");
        storage.Setup(s => s.GetPresignedGetUrlAsync($"{file.MinioKey}_w800", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://minio/w800");

        var result = await sut.GetDownloadUrlAsync(GuildId, ChannelId, FileId);

        result.BlurHash.Should().Be("LKO2:N%2Tw=w]~RBVZRi");
        result.Srcset.Should().NotBeNull();
        result.Srcset!.Select(v => v.Width).Should().Equal(400, 800);
        result.Srcset.Should().Contain(v => v.Width == 400 && v.Url == "http://minio/w400");
        result.Srcset.Should().Contain(v => v.Width == 800 && v.Url == "http://minio/w800");
    }

    [Fact]
    public async Task GetDownloadUrl_NoVariants_LeavesSrcsetNull()
    {
        var (sut, files, _, _) = BuildSut();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(ConfirmedFile());

        var result = await sut.GetDownloadUrlAsync(GuildId, ChannelId, FileId);

        result.Srcset.Should().BeNull();
        result.BlurHash.Should().BeNull();
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
    public async Task GetDownloadUrl_MintsAThumbnailUrlOnlyWhenAKeyIsSet()
    {
        var (sut, files, _, _) = BuildSut();
        var plain = ConfirmedFile();
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(plain);
        (await sut.GetDownloadUrlAsync(GuildId, ChannelId, FileId)).ThumbnailUrl.Should().BeNull();

        var withThumb = ConfirmedFile();
        withThumb.ThumbnailKey = $"{withThumb.MinioKey}_thumb";
        files.Setup(f => f.GetByIdAsync(FileId)).ReturnsAsync(withThumb);
        (await sut.GetDownloadUrlAsync(GuildId, ChannelId, FileId)).ThumbnailUrl
            .Should().Be("http://minio/download");
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

    // ---- Batch download ---------------------------------------------------

    [Fact]
    public async Task GetDownloadUrls_ReturnsOnlyConfirmedFilesScopedToTheChannel()
    {
        var (sut, files, _, _) = BuildSut();
        var confirmed = ConfirmedFile();
        var pending = PendingFile();
        pending.Id = FileId + 1;
        var foreign = ConfirmedFile();
        foreign.Id = FileId + 2;
        foreign.ChannelId = ChannelId + 1;
        files
            .Setup(f => f.GetByIdsAsync(It.IsAny<IReadOnlyCollection<long>>()))
            .ReturnsAsync([confirmed, pending, foreign]);

        var result = await sut.GetDownloadUrlsAsync(
            GuildId, ChannelId, [FileId, FileId + 1, FileId + 2, FileId + 3]);

        // Pending, foreign-channel, and unknown ids are silently omitted — never a throw.
        result.Should().ContainSingle().Which.Id.Should().Be(FileId);
        result[0].Url.Should().Be("http://minio/download");
    }

    [Fact]
    public async Task GetDownloadUrls_EmptyInput_ReturnsEmptyWithoutTouchingTheRepo()
    {
        var (sut, files, _, _) = BuildSut();

        var result = await sut.GetDownloadUrlsAsync(GuildId, ChannelId, []);

        result.Should().BeEmpty();
        files.Verify(f => f.GetByIdsAsync(It.IsAny<IReadOnlyCollection<long>>()), Times.Never);
    }
}
