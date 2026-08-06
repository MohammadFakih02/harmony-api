using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for the OrphanFileSweepService sweep: it best-effort deletes each orphan's
/// object then removes the rows, and a failed object delete must not abort the row cleanup.
/// </summary>
public class OrphanFileSweepServiceTests
{
    private static (
        OrphanFileSweepService sut,
        Mock<IFileAttachmentRepository> repo,
        Mock<IFileStorageService> storage
    ) BuildSut()
    {
        var repo = new Mock<IFileAttachmentRepository>();
        var storage = new Mock<IFileStorageService>();

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(IFileAttachmentRepository))).Returns(repo.Object);
        sp.Setup(s => s.GetService(typeof(IFileStorageService))).Returns(storage.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var sut = new OrphanFileSweepService(
            scopeFactory.Object,
            NullLogger<OrphanFileSweepService>.Instance
        );

        return (sut, repo, storage);
    }

    private static FileAttachment Orphan(long id, string key) =>
        new()
        {
            Id = id,
            MinioKey = key,
            IsConfirmed = false,
            CreatedAt = 1,
        };

    [Fact]
    public async Task RunOnce_DeletesObjectsAndRemovesRows()
    {
        var (sut, repo, storage) = BuildSut();
        var orphans = new List<FileAttachment> { Orphan(1, "a"), Orphan(2, "b") };
        repo.Setup(r => r.GetUnconfirmedOlderThanAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(orphans);

        var count = await sut.RunOnceAsync();

        count.Should().Be(2);
        storage.Verify(s => s.DeleteObjectAsync("a", It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(s => s.DeleteObjectAsync("b", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.RemoveRange(orphans), Times.Once);
        repo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task RunOnce_WithNoOrphans_DoesNothing()
    {
        var (sut, repo, storage) = BuildSut();
        repo.Setup(r => r.GetUnconfirmedOlderThanAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<FileAttachment>());

        var count = await sut.RunOnceAsync();

        count.Should().Be(0);
        storage.Verify(
            s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        repo.Verify(r => r.RemoveRange(It.IsAny<IEnumerable<FileAttachment>>()), Times.Never);
        repo.Verify(r => r.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task RunOnce_FailedObjectDelete_StillRemovesRows()
    {
        var (sut, repo, storage) = BuildSut();
        var orphans = new List<FileAttachment> { Orphan(1, "a"), Orphan(2, "b") };
        repo.Setup(r => r.GetUnconfirmedOlderThanAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(orphans);
        storage
            .Setup(s => s.DeleteObjectAsync("a", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"));

        var count = await sut.RunOnceAsync();

        count.Should().Be(2);
        storage.Verify(s => s.DeleteObjectAsync("b", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.RemoveRange(orphans), Times.Once);
        repo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }
}
