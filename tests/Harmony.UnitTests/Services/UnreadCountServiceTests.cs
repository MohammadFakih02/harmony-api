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
        Mock<IHubBroadcaster> broadcaster
    ) BuildSut(bool redisConnected)
    {
        var provider = new Mock<IRedisConnectionProvider>();
        provider.Setup(p => p.IsConnected).Returns(redisConnected);
        provider
            .Setup(p => p.Connection)
            .Returns((StackExchange.Redis.IConnectionMultiplexer?)null);

        var readState = new Mock<IReadStateRepository>();
        var broadcaster = new Mock<IHubBroadcaster>();

        // Default: every channel is viewable (the read path gates on HasAsync now).
        var permissions = new Mock<IPermissionService>();
        permissions
            .Setup(p => p.HasAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<Harmony.Domain.Domain.Enums.Permission>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var dms = new Mock<IDirectMessageRepository>();

        var sut = new RedisUnreadCountService(
            provider.Object,
            readState.Object,
            broadcaster.Object,
            permissions.Object,
            dms.Object,
            NullLogger<RedisUnreadCountService>.Instance
        );

        return (sut, readState, broadcaster);
    }

    [Fact]
    public async Task IncrementForChannelAsync_GuildChannel_WhenRedisDown_SendsNoPerUserUpdateAndDoesNotThrow()
    {
        var (sut, _, broadcaster) = BuildSut(redisConnected: false);

        var act = () => sut.IncrementForChannelAsync(guildId: 1, channelId: 2, senderUserId: 99);

        // The durable INCR is skipped (Redis down), but the guild path never uses the per-user
        // UnreadCountUpdated fan-out — that's the whole D3 point. The live ChannelActivity ping is
        // independent of Redis and may still fire; it must not throw.
        await act.Should().NotThrowAsync();
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
        var (sut, _, _) = BuildSut(redisConnected: false);

        var result = await sut.GetUnreadForUserAsync(
            userId: 5,
            channelGuildMap: new Dictionary<long, long> { [10] = 1, [11] = 1, [12] = 1 }
        );

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadAsync_WritesReadStateFirst_ThenBroadcastsZero_EvenWhenRedisDown()
    {
        var (sut, readState, broadcaster) = BuildSut(redisConnected: false);

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
