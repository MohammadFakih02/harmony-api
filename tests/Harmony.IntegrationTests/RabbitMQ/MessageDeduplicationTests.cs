using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Harmony.IntegrationTests.RabbitMQ;

/// <summary>
/// Integration tests for <see cref="RedisMessageDeduplicator"/> against a real Redis instance.
///
/// These tests verify the atomic SET NX behaviour: exactly one caller wins
/// the race, all others are correctly identified as duplicates.
///
/// Requires Redis running on localhost:6379 (same as the docker-compose setup).
/// Keys are cleaned up after each test.
/// </summary>
public class MessageDeduplicationTests : IAsyncLifetime
{
    private IConnectionMultiplexer _redis = null!;
    private IDatabase _db = null!;
    private RedisMessageDeduplicator _sut = null!;

    // Track keys created during tests so we can clean up
    private readonly List<string> _keysToCleanup = [];

    public async Task InitializeAsync()
    {
        var options = ConfigurationOptions.Parse("localhost:6379,abortConnect=false");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        _db = _redis.GetDatabase();

        // Wrap the real connection in a mocked provider interface
        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock.Setup(p => p.Connection).Returns(_redis);
        providerMock.Setup(p => p.IsConnected).Returns(true);

        _sut = new RedisMessageDeduplicator(
            providerMock.Object,
            NullLogger<RedisMessageDeduplicator>.Instance
        );
    }

    public async Task DisposeAsync()
    {
        // Clean up all keys written during this test run
        if (_keysToCleanup.Count > 0)
        {
            var keys = _keysToCleanup.Select(k => (RedisKey)k).ToArray();
            await _db.KeyDeleteAsync(keys);
        }

        await _redis.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<bool> CheckAsync(string eventType, long messageId)
    {
        _keysToCleanup.Add(RedisMessageDeduplicator.BuildKey(eventType, messageId));
        return await _sut.IsDuplicateAsync(eventType, messageId);
    }

    // -------------------------------------------------------------------------
    // Basic behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FirstCall_ShouldReturnFalse_AndSetKey()
    {
        var messageId = UniqueId();

        var result = await CheckAsync(IMessageDeduplicator.Sent, messageId);

        result.Should().BeFalse("first call — not a duplicate");

        // Verify key exists in Redis
        var key = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, messageId);
        var exists = await _db.KeyExistsAsync(key);
        exists.Should().BeTrue("key should have been set by first call");
    }

    [Fact]
    public async Task SecondCall_ShouldReturnTrue_KeyAlreadyExists()
    {
        var messageId = UniqueId();

        var first = await CheckAsync(IMessageDeduplicator.Sent, messageId);
        var second = await CheckAsync(IMessageDeduplicator.Sent, messageId);

        first.Should().BeFalse("first call is not a duplicate");
        second.Should().BeTrue("second call is a duplicate");
    }

    [Fact]
    public async Task ThirdAndSubsequentCalls_ShouldAllReturnTrue()
    {
        var messageId = UniqueId();

        await CheckAsync(IMessageDeduplicator.Sent, messageId);

        for (var i = 0; i < 5; i++)
        {
            var result = await CheckAsync(IMessageDeduplicator.Sent, messageId);
            result.Should().BeTrue($"call {i + 2} should be detected as duplicate");
        }
    }

    // -------------------------------------------------------------------------
    // Event type independence
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SentKey_ShouldNotBlock_DeletedKey_ForSameMessageId()
    {
        var messageId = UniqueId();
        _keysToCleanup.Add(RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, messageId));
        _keysToCleanup.Add(
            RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Deleted, messageId)
        );

        var sentResult = await _sut.IsDuplicateAsync(IMessageDeduplicator.Sent, messageId);
        var deletedResult = await _sut.IsDuplicateAsync(IMessageDeduplicator.Deleted, messageId);

        sentResult.Should().BeFalse("first sent call");
        deletedResult.Should().BeFalse("deleted has its own key — independent");
    }

    [Fact]
    public async Task AllThreeEventTypes_AreTrackedIndependently()
    {
        var messageId = UniqueId();
        _keysToCleanup.Add(RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, messageId));
        _keysToCleanup.Add(
            RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Deleted, messageId)
        );
        _keysToCleanup.Add(
            RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Edited, messageId)
        );

        // First call for each type — none should be duplicates
        var r1 = await _sut.IsDuplicateAsync(IMessageDeduplicator.Sent, messageId);
        var r2 = await _sut.IsDuplicateAsync(IMessageDeduplicator.Deleted, messageId);
        var r3 = await _sut.IsDuplicateAsync(IMessageDeduplicator.Edited, messageId);

        r1.Should().BeFalse();
        r2.Should().BeFalse();
        r3.Should().BeFalse();

        // Second call for each type — all should be duplicates
        var r4 = await _sut.IsDuplicateAsync(IMessageDeduplicator.Sent, messageId);
        var r5 = await _sut.IsDuplicateAsync(IMessageDeduplicator.Deleted, messageId);
        var r6 = await _sut.IsDuplicateAsync(IMessageDeduplicator.Edited, messageId);

        r4.Should().BeTrue();
        r5.Should().BeTrue();
        r6.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // TTL behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Key_ShouldHaveTtlClose_To60Seconds()
    {
        var messageId = UniqueId();
        var key = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, messageId);
        _keysToCleanup.Add(key);

        await _sut.IsDuplicateAsync(IMessageDeduplicator.Sent, messageId);

        var ttl = await _db.KeyTimeToLiveAsync(key);

        ttl.Should().NotBeNull("key should have a TTL set");
        ttl!.Value.Should().BeCloseTo(TimeSpan.FromSeconds(60), precision: TimeSpan.FromSeconds(5));
    }

    // -------------------------------------------------------------------------
    // Out-of-order requeue counter — bounds edit-before-sent requeues
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IncrementRequeueCount_FirstCall_Returns1_AndSetsTtl()
    {
        var messageId = UniqueId();
        var key = RedisMessageDeduplicator.BuildRequeueKey(IMessageDeduplicator.Edited, messageId);
        _keysToCleanup.Add(key);

        var count = await _sut.IncrementRequeueCountAsync(IMessageDeduplicator.Edited, messageId);

        count.Should().Be(1, "first requeue attempt");

        var ttl = await _db.KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull("the counter must expire so it can't bias a later distinct edit");
        ttl!.Value.Should().BeCloseTo(TimeSpan.FromMinutes(2), precision: TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task IncrementRequeueCount_Accumulates_AcrossCalls()
    {
        var messageId = UniqueId();
        _keysToCleanup.Add(
            RedisMessageDeduplicator.BuildRequeueKey(IMessageDeduplicator.Edited, messageId)
        );

        var first = await _sut.IncrementRequeueCountAsync(IMessageDeduplicator.Edited, messageId);
        var second = await _sut.IncrementRequeueCountAsync(IMessageDeduplicator.Edited, messageId);
        var third = await _sut.IncrementRequeueCountAsync(IMessageDeduplicator.Edited, messageId);

        first.Should().Be(1);
        second.Should().Be(2);
        third.Should().Be(3);
    }

    [Fact]
    public async Task IncrementRequeueCount_IsIndependent_PerMessage()
    {
        var idA = UniqueId();
        var idB = UniqueId();
        _keysToCleanup.Add(RedisMessageDeduplicator.BuildRequeueKey(IMessageDeduplicator.Edited, idA));
        _keysToCleanup.Add(RedisMessageDeduplicator.BuildRequeueKey(IMessageDeduplicator.Edited, idB));

        await _sut.IncrementRequeueCountAsync(IMessageDeduplicator.Edited, idA);
        await _sut.IncrementRequeueCountAsync(IMessageDeduplicator.Edited, idA);
        var bCount = await _sut.IncrementRequeueCountAsync(IMessageDeduplicator.Edited, idB);

        bCount.Should().Be(1, "a different message's counter is not affected");
    }

    // -------------------------------------------------------------------------
    // Concurrency — simulates two consumer instances racing on the same message
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentCalls_ShouldAllowExactlyOneToProcess()
    {
        var messageId = UniqueId();
        var key = RedisMessageDeduplicator.BuildKey(IMessageDeduplicator.Sent, messageId);
        _keysToCleanup.Add(key);

        // Fire 10 concurrent calls simulating 10 consumer instances racing
        var tasks = Enumerable
            .Range(0, 10)
            .Select(_ => _sut.IsDuplicateAsync(IMessageDeduplicator.Sent, messageId))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Exactly one should return false (won the SET NX race)
        results.Count(r => r == false).Should().Be(1, "exactly one caller should win the race");

        // All others should return true (lost the race, detected as duplicate)
        results.Count(r => r == true).Should().Be(9);
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a unique long ID for test isolation.
    /// Uses a timestamp offset to avoid collisions with real Snowflake IDs.
    /// </summary>
    private static long UniqueId() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
}
