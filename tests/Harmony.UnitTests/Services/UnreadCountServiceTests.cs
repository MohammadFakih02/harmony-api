using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Harmony.UnitTests.Services;

public class UnreadCountServiceTests
{
    private static (
        RedisUnreadCountService sut,
        Mock<IReadStateRepository> readState,
        Mock<IHubBroadcaster> broadcaster,
        Mock<IGuildRepository> guilds
    ) BuildSut(bool redisConnected)
    {
        var provider = new Mock<IRedisConnectionProvider>();
        provider.Setup(p => p.IsConnected).Returns(redisConnected);
        provider
            .Setup(p => p.Connection)
            .Returns((StackExchange.Redis.IConnectionMultiplexer?)null);

        var guilds = new Mock<IGuildRepository>();
        var readState = new Mock<IReadStateRepository>();
        var broadcaster = new Mock<IHubBroadcaster>();

        var sut = new RedisUnreadCountService(
            provider.Object,
            guilds.Object,
            readState.Object,
            broadcaster.Object,
            NullLogger<RedisUnreadCountService>.Instance
        );

        return (sut, readState, broadcaster, guilds);
    }

    [Fact]
    public async Task IncrementForChannelAsync_WhenRedisDown_DoesNotBroadcastOrThrow()
    {
        var (sut, _, broadcaster, guilds) = BuildSut(redisConnected: false);

        var act = () => sut.IncrementForChannelAsync(guildId: 1, channelId: 2, senderUserId: 99);

        await act.Should().NotThrowAsync();
        guilds.Verify(g => g.GetMemberIdsAsync(It.IsAny<long>()), Times.Never);
        broadcaster.Verify(
            b =>
                b.BroadcastUnreadCountAsync(
                    It.IsAny<long>(),
                    It.IsAny<UnreadCountPayload>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task GetUnreadForUserAsync_WhenRedisDown_ReturnsEmpty()
    {
        var (sut, _, _, _) = BuildSut(redisConnected: false);

        var result = await sut.GetUnreadForUserAsync(userId: 5, channelIds: [10, 11, 12]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadAsync_WritesReadStateFirst_ThenBroadcastsZero_EvenWhenRedisDown()
    {
        var (sut, readState, broadcaster, _) = BuildSut(redisConnected: false);

        await sut.MarkReadAsync(userId: 5, guildId: 1, channelId: 10, lastReadMessageId: 9000);

        // Truth write always happens, regardless of Redis.
        readState.Verify(
            r => r.MarkAsReadAsync(5, 10, 9000, It.IsAny<CancellationToken>()),
            Times.Once
        );

        // Multi-device sync still fires with an absolute zero.
        broadcaster.Verify(
            b =>
                b.BroadcastUnreadCountAsync(
                    5,
                    It.Is<UnreadCountPayload>(p =>
                        p.ChannelId == 10 && p.GuildId == 1 && p.UnreadCount == 0
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }
}
