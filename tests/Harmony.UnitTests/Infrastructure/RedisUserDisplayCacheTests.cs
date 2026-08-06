using System.Text.Json;
using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Harmony.UnitTests.Infrastructure;

public class RedisUserDisplayCacheTests
{
    private static (RedisUserDisplayCache sut, Mock<IDatabase> dbMock) BuildSut(
        bool redisConnected = true
    )
    {
        var dbMock = new Mock<IDatabase>();
        var multiplexerMock = new Mock<IConnectionMultiplexer>();
        multiplexerMock.Setup(m => m.IsConnected).Returns(redisConnected);
        multiplexerMock
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock
            .Setup(p => p.Connection)
            .Returns(redisConnected ? multiplexerMock.Object : null);
        providerMock.Setup(p => p.IsConnected).Returns(redisConnected);

        var sut = new RedisUserDisplayCache(
            providerMock.Object,
            NullLogger<RedisUserDisplayCache>.Instance
        );
        return (sut, dbMock);
    }

    // -------------------------------------------------------------------------
    // Key format
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(12345L, "userdisplay:12345")]
    [InlineData(1L, "userdisplay:1")]
    public void BuildKey_ShouldProduceCorrectFormat(long userId, string expected) =>
        RedisUserDisplayCache.BuildKey(userId).Should().Be(expected);

    // -------------------------------------------------------------------------
    // GetAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ShouldReturnValue_OnHit()
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisUserDisplayCache.BuildKey(7L);
        // The on-wire shape uses short property names {U, A}.
        var json = JsonSerializer.Serialize(new { U = "alice", A = "avatars/7.webp" });
        dbMock
            .Setup(d => d.StringGetAsync(It.Is<RedisKey>(k => k.ToString() == key), It.IsAny<CommandFlags>()))
            .ReturnsAsync(json);

        var result = await sut.GetAsync(7L);

        result.Should().NotBeNull();
        result!.Value.Username.Should().Be("alice");
        result.Value.AvatarKey.Should().Be("avatars/7.webp");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_OnMiss()
    {
        var (sut, dbMock) = BuildSut();
        dbMock
            .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        (await sut.GetAsync(8L)).Should().BeNull("a miss falls back to the source of truth");
    }

    [Fact]
    public async Task GetAsync_ShouldFailOpen_WhenDisconnected()
    {
        var (sut, dbMock) = BuildSut(redisConnected: false);

        (await sut.GetAsync(9L)).Should().BeNull("fail-open when Redis is unavailable");
        dbMock.Verify(
            d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetAsync_ShouldFailOpen_WhenRedisThrows()
    {
        var (sut, dbMock) = BuildSut();
        dbMock
            .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        (await sut.GetAsync(10L)).Should().BeNull("fail-open on a Redis error");
    }

    // -------------------------------------------------------------------------
    // SetAsync — round-trips through GetAsync's shape, with the 5-minute TTL
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetAsync_ShouldWriteWithFiveMinuteTtl()
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisUserDisplayCache.BuildKey(11L);
        dbMock
            .Setup(d =>
                d.StringSetAsync(
                    It.Is<RedisKey>(k => k.ToString() == key),
                    It.IsAny<RedisValue>(),
                    It.Is<TimeSpan?>(t => t == TimeSpan.FromMinutes(5)),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()
                )
            )
            .ReturnsAsync(true)
            .Verifiable();

        await sut.SetAsync(11L, new UserDisplay("bob", null));

        dbMock.Verify();
    }

    [Fact]
    public async Task SetAsync_ShouldNoOp_WhenDisconnected()
    {
        var (sut, dbMock) = BuildSut(redisConnected: false);

        await sut.SetAsync(12L, new UserDisplay("carol", "k"));

        dbMock.Verify(
            d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<When>(), It.IsAny<CommandFlags>()),
            Times.Never
        );
    }

    // -------------------------------------------------------------------------
    // InvalidateAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidateAsync_ShouldDeleteKey()
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisUserDisplayCache.BuildKey(13L);
        dbMock
            .Setup(d => d.KeyDeleteAsync(It.Is<RedisKey>(k => k.ToString() == key), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true)
            .Verifiable();

        await sut.InvalidateAsync(13L);

        dbMock.Verify(
            d => d.KeyDeleteAsync(It.Is<RedisKey>(k => k.ToString() == key), It.IsAny<CommandFlags>()),
            Times.Once
        );
    }

    [Fact]
    public async Task InvalidateAsync_ShouldNoOp_WhenDisconnected()
    {
        var (sut, dbMock) = BuildSut(redisConnected: false);

        await sut.InvalidateAsync(14L);

        dbMock.Verify(
            d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()),
            Times.Never
        );
    }
}
