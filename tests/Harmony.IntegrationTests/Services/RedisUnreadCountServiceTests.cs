using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Harmony.IntegrationTests.RabbitMQ;

/// <summary>
/// Integration tests for <see cref="RedisUnreadCountService"/> against real Redis.
///
/// The end-to-end flow tests (UnreadCountFlowTests) prove the pipeline wiring;
/// these isolate the service's own guarantees: pipelined INCR correctness,
/// sender exclusion, absolute counts in the broadcast, truth-first mark-as-read
/// ordering, and the MGET read path. Repositories and the broadcaster are mocked.
///
/// Requires Redis on localhost:6379. Keys are cleaned up after each test.
/// </summary>
public class UnreadCountServiceTests : IAsyncLifetime
{
    private IConnectionMultiplexer _redis = null!;
    private IDatabase _db = null!;
    private Mock<IGuildRepository> _guilds = null!;
    private Mock<IReadStateRepository> _readStates = null!;
    private Mock<IHubBroadcaster> _broadcaster = null!;
    private RedisUnreadCountService _sut = null!;

    private readonly List<string> _keysToCleanup = [];

    public async Task InitializeAsync()
    {
        var options = ConfigurationOptions.Parse("localhost:6379,abortConnect=false");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        _db = _redis.GetDatabase();

        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock.Setup(p => p.Connection).Returns(_redis);
        providerMock.Setup(p => p.IsConnected).Returns(true);

        _guilds = new Mock<IGuildRepository>();
        _readStates = new Mock<IReadStateRepository>();
        _broadcaster = new Mock<IHubBroadcaster>();

        _sut = new RedisUnreadCountService(
            providerMock.Object,
            _guilds.Object,
            _readStates.Object,
            _broadcaster.Object,
            NullLogger<RedisUnreadCountService>.Instance
        );
    }

    public async Task DisposeAsync()
    {
        if (_keysToCleanup.Count > 0)
            await _db.KeyDeleteAsync(_keysToCleanup.Select(k => (RedisKey)k).ToArray());

        await _redis.DisposeAsync();
    }

    private void Track(params long[] userIdsForChannel)
    {
        // channelId is fixed per test below; tracked at call sites.
    }

    private static long UniqueChannel() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);

    // -------------------------------------------------------------------------
    // IncrementForChannelAsync — pipelining, exclusion, absolute counts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IncrementForChannel_ShouldIncrementEveryRecipient_ExceptSender()
    {
        var channelId = UniqueChannel();
        const long sender = 99;
        var members = new List<long> { sender, 1, 2, 3 };
        _guilds.Setup(g => g.GetMemberIdsAsync(It.IsAny<long>())).ReturnsAsync(members);

        foreach (var uid in members)
            _keysToCleanup.Add(RedisUnreadCountService.UnreadKey(uid, channelId));

        await _sut.IncrementForChannelAsync(guildId: 1, channelId: channelId, senderUserId: sender);

        // Recipients incremented to 1
        foreach (var uid in new[] { 1L, 2L, 3L })
        {
            var val = await _db.StringGetAsync(RedisUnreadCountService.UnreadKey(uid, channelId));
            val.ToString().Should().Be("1", $"user {uid} is a recipient");
        }

        // Sender NOT incremented — key absent
        var senderKey = RedisUnreadCountService.UnreadKey(sender, channelId);
        (await _db.KeyExistsAsync(senderKey)).Should().BeFalse("sender is excluded");
    }

    [Fact]
    public async Task IncrementForChannel_ShouldBroadcastAbsoluteCount_ToEachRecipient()
    {
        var channelId = UniqueChannel();
        var members = new List<long> { 99, 5 };
        _guilds.Setup(g => g.GetMemberIdsAsync(It.IsAny<long>())).ReturnsAsync(members);
        _keysToCleanup.Add(RedisUnreadCountService.UnreadKey(5, channelId));

        await _sut.IncrementForChannelAsync(guildId: 7, channelId: channelId, senderUserId: 99);

        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(
                5,
                It.Is<UnreadCountPayload>(p =>
                    p.ChannelId == channelId && p.GuildId == 7 && p.UnreadCount == 1
                ),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );

        // Sender never broadcast to
        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(99, It.IsAny<UnreadCountPayload>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task IncrementForChannel_CalledTwice_ShouldBroadcastCountTwo_OnSecond()
    {
        var channelId = UniqueChannel();
        var members = new List<long> { 99, 8 };
        _guilds.Setup(g => g.GetMemberIdsAsync(It.IsAny<long>())).ReturnsAsync(members);
        _keysToCleanup.Add(RedisUnreadCountService.UnreadKey(8, channelId));

        await _sut.IncrementForChannelAsync(1, channelId, 99);
        await _sut.IncrementForChannelAsync(1, channelId, 99);

        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(
                8,
                It.Is<UnreadCountPayload>(p => p.UnreadCount == 2),
                It.IsAny<CancellationToken>()
            ),
            Times.Once,
            "second increment must broadcast the absolute count 2"
        );
    }

    [Fact]
    public async Task IncrementForChannel_OneBadBroadcast_ShouldNotAbortRemainingRecipients()
    {
        var channelId = UniqueChannel();
        var members = new List<long> { 99, 10, 11 };
        _guilds.Setup(g => g.GetMemberIdsAsync(It.IsAny<long>())).ReturnsAsync(members);
        _keysToCleanup.Add(RedisUnreadCountService.UnreadKey(10, channelId));
        _keysToCleanup.Add(RedisUnreadCountService.UnreadKey(11, channelId));

        // User 10's broadcast throws; user 11 must still get theirs.
        _broadcaster
            .Setup(b => b.BroadcastUnreadCountAsync(10, It.IsAny<UnreadCountPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("transient SignalR failure"));

        var act = () => _sut.IncrementForChannelAsync(1, channelId, 99);
        await act.Should().NotThrowAsync("a single bad push must never abort the fan-out");

        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(11, It.IsAny<UnreadCountPayload>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "user 11 must still receive their push after user 10's failed"
        );

        // Both keys still incremented in Redis regardless of broadcast outcome.
        (await _db.StringGetAsync(RedisUnreadCountService.UnreadKey(10, channelId))).ToString().Should().Be("1");
        (await _db.StringGetAsync(RedisUnreadCountService.UnreadKey(11, channelId))).ToString().Should().Be("1");
    }

    [Fact]
    public async Task IncrementForChannel_WhenOnlySenderIsMember_ShouldDoNothing()
    {
        var channelId = UniqueChannel();
        _guilds.Setup(g => g.GetMemberIdsAsync(It.IsAny<long>())).ReturnsAsync([99]);

        await _sut.IncrementForChannelAsync(1, channelId, senderUserId: 99);

        (await _db.KeyExistsAsync(RedisUnreadCountService.UnreadKey(99, channelId))).Should().BeFalse();
        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(It.IsAny<long>(), It.IsAny<UnreadCountPayload>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    // -------------------------------------------------------------------------
    // MarkReadAsync — truth-first ordering, cache clear, zero broadcast
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MarkRead_ShouldWriteReadState_ClearKey_AndBroadcastZero()
    {
        var channelId = UniqueChannel();
        var key = RedisUnreadCountService.UnreadKey(5, channelId);
        _keysToCleanup.Add(key);

        // Pre-seed a non-zero count.
        await _db.StringSetAsync(key, "7");

        await _sut.MarkReadAsync(userId: 5, guildId: 2, channelId: channelId, lastReadMessageId: 9000);

        // Truth written
        _readStates.Verify(
            r => r.MarkAsReadAsync(5, channelId, 9000, It.IsAny<CancellationToken>()),
            Times.Once
        );

        // Cache key cleared
        (await _db.KeyExistsAsync(key)).Should().BeFalse("mark-as-read deletes the unread cache key");

        // Zero broadcast for multi-device sync
        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(
                5,
                It.Is<UnreadCountPayload>(p => p.ChannelId == channelId && p.UnreadCount == 0),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task MarkRead_WhenReadStateWriteThrows_ShouldNotSwallow()
    {
        var channelId = UniqueChannel();
        _readStates
            .Setup(r => r.MarkAsReadAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("scylla write failed"));

        var act = () => _sut.MarkReadAsync(5, 1, channelId, 9000);

        await act.Should()
            .ThrowAsync<Exception>("the source-of-truth write must not be swallowed — the caller has to know");

        // Cache must NOT have been cleared, since truth failed first.
        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(It.IsAny<long>(), It.IsAny<UnreadCountPayload>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no zero-broadcast when the truth write never succeeded"
        );
    }

    // -------------------------------------------------------------------------
    // GetUnreadForUserAsync — MGET, count>0 filter, absent keys
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetUnread_ShouldReturnOnlyChannels_WithCountGreaterThanZero()
    {
        var ch1 = UniqueChannel();
        var ch2 = UniqueChannel();
        var ch3 = UniqueChannel();
        const long userId = 42;

        var k1 = RedisUnreadCountService.UnreadKey(userId, ch1);
        var k2 = RedisUnreadCountService.UnreadKey(userId, ch2);
        _keysToCleanup.Add(k1);
        _keysToCleanup.Add(k2);

        await _db.StringSetAsync(k1, "3");
        await _db.StringSetAsync(k2, "5");
        // ch3 intentionally absent

        var result = await _sut.GetUnreadForUserAsync(userId, [ch1, ch2, ch3]);

        result.Should().HaveCount(2);
        result[ch1].Should().Be(3);
        result[ch2].Should().Be(5);
        result.Should().NotContainKey(ch3, "absent keys are not unread");
    }

    [Fact]
    public async Task GetUnread_WithNoChannels_ShouldReturnEmpty()
    {
        var result = await _sut.GetUnreadForUserAsync(userId: 1, channelIds: []);
        result.Should().BeEmpty();
    }
}