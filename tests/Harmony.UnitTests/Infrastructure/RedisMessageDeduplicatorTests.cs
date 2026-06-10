using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Harmony.UnitTests.Infrastructure;

public class RedisMessageDeduplicatorTests
{
    // -------------------------------------------------------------------------
    // Setup and Mock Helpers
    // -------------------------------------------------------------------------

    private static (RedisMessageDeduplicator sut, Mock<IDatabase> dbMock) BuildSut(
        bool redisConnected = true
    )
    {
        var dbMock = new Mock<IDatabase>();
        var multiplexerMock = new Mock<IConnectionMultiplexer>();

        multiplexerMock.Setup(m => m.IsConnected).Returns(redisConnected);
        multiplexerMock
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        // Mock our new provider interface
        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock
            .Setup(p => p.Connection)
            .Returns(redisConnected ? multiplexerMock.Object : null);
        providerMock.Setup(p => p.IsConnected).Returns(redisConnected);

        // Inject the mocked provider
        var sut = new RedisMessageDeduplicator(
            providerMock.Object,
            NullLogger<RedisMessageDeduplicator>.Instance
        );

        return (sut, dbMock);
    }

    private static void SetupKeyAbsent(Mock<IDatabase> dbMock, string key) =>
        dbMock
            .Setup(d =>
                d.StringSetAsync(
                    It.Is<RedisKey>(k => k.ToString() == key), // Safe string conversion
                    It.Is<RedisValue>(v => v.ToString() == "1"), // Safe string conversion
                    It.IsAny<TimeSpan?>(),
                    When.NotExists,
                    CommandFlags.None
                )
            )
            .ReturnsAsync(true); // SET NX succeeded → key was absent → first time

    private static void SetupKeyPresent(Mock<IDatabase> dbMock, string key) =>
        dbMock
            .Setup(d =>
                d.StringSetAsync(
                    It.Is<RedisKey>(k => k.ToString() == key),
                    It.Is<RedisValue>(v => v.ToString() == "1"),
                    It.IsAny<TimeSpan?>(),
                    When.NotExists,
                    CommandFlags.None
                )
            )
            .ReturnsAsync(false); // SET NX failed → key already existed → duplicate

    // -------------------------------------------------------------------------
    // Key format
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("sent", 12345L, "dedup:msg:sent:12345")]
    [InlineData("deleted", 99999L, "dedup:msg:deleted:99999")]
    [InlineData("edited", 1L, "dedup:msg:edited:1")]
    public void BuildKey_ShouldProduceCorrectFormat(
        string eventType,
        long messageId,
        string expected
    )
    {
        var key = RedisMessageDeduplicator.BuildKey(eventType, messageId);
        key.Should().Be(expected);
    }

    // -------------------------------------------------------------------------
    // First-time processing (not a duplicate)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsDuplicateAsync_ShouldReturnFalse_WhenKeyDoesNotExist()
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, 1001L);
        SetupKeyAbsent(dbMock, key);

        var result = await sut.IsDuplicateAsync(IMessageDeduplicator.Sent, 1001L);

        result.Should().BeFalse("key was absent — this is the first processing");
    }

    [Theory]
    [InlineData(IMessageDeduplicator.Sent)]
    [InlineData(IMessageDeduplicator.Deleted)]
    [InlineData(IMessageDeduplicator.Edited)]
    public async Task IsDuplicateAsync_ShouldReturnFalse_ForAllEventTypes_WhenKeyAbsent(
        string eventType
    )
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisMessageDeduplicator.BuildKey(eventType, 2001L);
        SetupKeyAbsent(dbMock, key);

        var result = await sut.IsDuplicateAsync(eventType, 2001L);

        result.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Duplicate detection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsDuplicateAsync_ShouldReturnTrue_WhenKeyAlreadyExists()
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, 3001L);
        SetupKeyPresent(dbMock, key);

        var result = await sut.IsDuplicateAsync(IMessageDeduplicator.Sent, 3001L);

        result.Should().BeTrue("key already existed — this is a redelivery");
    }

    [Theory]
    [InlineData(IMessageDeduplicator.Sent)]
    [InlineData(IMessageDeduplicator.Deleted)]
    [InlineData(IMessageDeduplicator.Edited)]
    public async Task IsDuplicateAsync_ShouldReturnTrue_ForAllEventTypes_WhenKeyPresent(
        string eventType
    )
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisMessageDeduplicator.BuildKey(eventType, 4001L);
        SetupKeyPresent(dbMock, key);

        var result = await sut.IsDuplicateAsync(eventType, 4001L);

        result.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Event types are independent — sent key does NOT block deleted/edited
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsDuplicateAsync_ShouldTreatEventTypesIndependently()
    {
        var (sut, dbMock) = BuildSut();
        var sentKey = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, 5001L);
        var deletedKey = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Deleted, 5001L);
        var editedKey = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Edited, 5001L);

        // sent key is present (already processed), deleted and edited are absent
        SetupKeyPresent(dbMock, sentKey);
        SetupKeyAbsent(dbMock, deletedKey);
        SetupKeyAbsent(dbMock, editedKey);

        var sentResult = await sut.IsDuplicateAsync(IMessageDeduplicator.Sent, 5001L);
        var deletedResult = await sut.IsDuplicateAsync(IMessageDeduplicator.Deleted, 5001L);
        var editedResult = await sut.IsDuplicateAsync(IMessageDeduplicator.Edited, 5001L);

        sentResult.Should().BeTrue("sent was already processed");
        deletedResult.Should().BeFalse("deleted has its own key — not a duplicate");
        editedResult.Should().BeFalse("edited has its own key — not a duplicate");
    }

    // -------------------------------------------------------------------------
    // Fail-open behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsDuplicateAsync_ShouldReturnFalse_WhenRedisIsNull()
    {
        // Mock the provider to return false/null
        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock.Setup(p => p.Connection).Returns((IConnectionMultiplexer?)null);
        providerMock.Setup(p => p.IsConnected).Returns(false);

        var sut = new RedisMessageDeduplicator(
            providerMock.Object, // Pass the mocked provider
            NullLogger<RedisMessageDeduplicator>.Instance
        );

        var result = await sut.IsDuplicateAsync(IMessageDeduplicator.Sent, 6001L);

        result.Should().BeFalse("fail-open: process the message when Redis is unavailable");
    }

    [Fact]
    public async Task IsDuplicateAsync_ShouldReturnFalse_WhenRedisIsDisconnected()
    {
        var (sut, _) = BuildSut(redisConnected: false);

        var result = await sut.IsDuplicateAsync(IMessageDeduplicator.Sent, 7001L);

        result.Should().BeFalse("fail-open: process the message when Redis is disconnected");
    }

    [Fact]
    public async Task IsDuplicateAsync_ShouldReturnFalse_WhenRedisThrows()
    {
        var dbMock = new Mock<IDatabase>();
        dbMock
            .Setup(d =>
                d.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()
                )
            )
            .ThrowsAsync(
                new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down")
            );

        var multiplexerMock = new Mock<IConnectionMultiplexer>();
        multiplexerMock.Setup(m => m.IsConnected).Returns(true);
        multiplexerMock
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock.Setup(p => p.Connection).Returns((IConnectionMultiplexer?)null);
        providerMock.Setup(p => p.IsConnected).Returns(false);

        var sut = new RedisMessageDeduplicator(
            providerMock.Object, // Pass the mocked provider
            NullLogger<RedisMessageDeduplicator>.Instance
        );

        var result = await sut.IsDuplicateAsync(IMessageDeduplicator.Sent, 8001L);

        result.Should().BeFalse("fail-open: process the message on Redis exception");
    }

    // -------------------------------------------------------------------------
    // SET NX uses correct TTL
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsDuplicateAsync_ShouldSet60SecondTtl()
    {
        var (sut, dbMock) = BuildSut();
        var key = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, 9001L);

        dbMock
            .Setup(d =>
                d.StringSetAsync(
                    It.Is<RedisKey>(k => k.ToString() == key),
                    It.Is<RedisValue>(v => v.ToString() == "1"),
                    It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(60)),
                    When.NotExists,
                    CommandFlags.None
                )
            )
            .ReturnsAsync(true)
            .Verifiable();

        await sut.IsDuplicateAsync(IMessageDeduplicator.Sent, 9001L);

        dbMock.Verify();
    }
}
