using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for the TrashPurgeService sweep (§5.71 #5): due guilds are hard-deleted with their
/// text channels' Scylla/search purged, individually-trashed channels are hard-deleted + purged, and
/// an empty trash is a no-op.
/// </summary>
public class TrashPurgeServiceTests
{
    private static (
        TrashPurgeService sut,
        Mock<IGuildRepository> guilds,
        Mock<IChannelRepository> channels,
        Mock<IMessagePublisher> publisher
    ) BuildSut()
    {
        var guilds = new Mock<IGuildRepository>();
        var channels = new Mock<IChannelRepository>();
        var publisher = new Mock<IMessagePublisher>();

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(IGuildRepository))).Returns(guilds.Object);
        sp.Setup(s => s.GetService(typeof(IChannelRepository))).Returns(channels.Object);
        sp.Setup(s => s.GetService(typeof(IMessagePublisher))).Returns(publisher.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        // Default: nothing due (each test overrides the arm it exercises).
        guilds.Setup(g => g.GetPurgeableAsync(It.IsAny<long>(), It.IsAny<int>())).ReturnsAsync([]);
        channels.Setup(c => c.GetPurgeableAsync(It.IsAny<long>(), It.IsAny<int>())).ReturnsAsync([]);

        var sut = new TrashPurgeService(scopeFactory.Object, NullLogger<TrashPurgeService>.Instance);
        return (sut, guilds, channels, publisher);
    }

    [Fact]
    public async Task RunOnce_PurgesDueGuild_AndCleansItsTextChannels()
    {
        var (sut, guilds, channels, publisher) = BuildSut();
        var guild = new Guild { Id = 10, Name = "gone", OwnerId = 1, DeletedAt = 1 };
        guilds.Setup(g => g.GetPurgeableAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync([guild]);
        channels
            .Setup(c => c.GetTextChannelIdsByGuildIncludingDeletedAsync(10))
            .ReturnsAsync([101L, 102L]);

        var purged = await sut.RunOnceAsync();

        purged.Should().Be(1);
        guilds.Verify(g => g.DeleteAsync(guild), Times.Once);
        guilds.Verify(g => g.SaveChangesAsync(), Times.Once);
        publisher.Verify(
            p => p.PublishChannelDeletedAsync(
                It.Is<ChannelDeletedEvent>(e => e.ChannelId == 101 && e.GuildId == 10),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        publisher.Verify(
            p => p.PublishChannelDeletedAsync(
                It.Is<ChannelDeletedEvent>(e => e.ChannelId == 102 && e.GuildId == 10),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task RunOnce_PurgesDueChannel_AndPublishesItsCleanup()
    {
        var (sut, guilds, channels, publisher) = BuildSut();
        var channel = new Channel { Id = 201, GuildId = 20, Name = "old", Type = "text", DeletedAt = 1 };
        channels.Setup(c => c.GetPurgeableAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync([channel]);

        var purged = await sut.RunOnceAsync();

        purged.Should().Be(1);
        channels.Verify(c => c.DeleteAsync(channel), Times.Once);
        channels.Verify(c => c.SaveChangesAsync(), Times.Once);
        publisher.Verify(
            p => p.PublishChannelDeletedAsync(
                It.Is<ChannelDeletedEvent>(e => e.ChannelId == 201 && e.GuildId == 20),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task RunOnce_WithEmptyTrash_DoesNothing()
    {
        var (sut, guilds, channels, publisher) = BuildSut();

        var purged = await sut.RunOnceAsync();

        purged.Should().Be(0);
        guilds.Verify(g => g.DeleteAsync(It.IsAny<Guild>()), Times.Never);
        channels.Verify(c => c.DeleteAsync(It.IsAny<Channel>()), Times.Never);
        publisher.Verify(
            p => p.PublishChannelDeletedAsync(
                It.IsAny<ChannelDeletedEvent>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }
}
