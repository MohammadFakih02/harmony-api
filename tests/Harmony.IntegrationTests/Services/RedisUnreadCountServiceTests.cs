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
/// Integration tests for <see cref="RedisUnreadCountService"/> against real Redis (the D3
/// read-time model).
///
/// <para>Guild channels are O(1) on the write path: one <c>channel:{id}:count</c> INCR plus a
/// single <c>ChannelActivity</c> ping — NO per-recipient <c>UnreadCountUpdated</c>. Unread is
/// computed read-time as <c>count − mark</c>, after a ViewChannel filter, with a lazy anchor for
/// never-marked channels. DMs keep the bounded per-participant path (per-user INCR + absolute
/// broadcast). These isolate those guarantees; repositories and the broadcaster are mocked.</para>
///
/// Requires Redis on localhost:6379. Keys are cleaned up after each test.
/// </summary>
public class UnreadCountServiceTests : IAsyncLifetime
{
    private IConnectionMultiplexer _redis = null!;
    private IDatabase _db = null!;
    private Mock<IReadStateRepository> _readStates = null!;
    private Mock<IHubBroadcaster> _broadcaster = null!;
    private Mock<IPermissionService> _permissions = null!;
    private Mock<IDirectMessageRepository> _dms = null!;
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

        _readStates = new Mock<IReadStateRepository>();
        _broadcaster = new Mock<IHubBroadcaster>();

        // Every channel is viewable by default; individual tests override for the filter case.
        _permissions = new Mock<IPermissionService>();
        _permissions
            .Setup(p => p.HasAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<Harmony.Domain.Domain.Enums.Permission>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _dms = new Mock<IDirectMessageRepository>();

        _sut = new RedisUnreadCountService(
            providerMock.Object,
            _readStates.Object,
            _broadcaster.Object,
            _permissions.Object,
            _dms.Object,
            NullLogger<RedisUnreadCountService>.Instance
        );
    }

    public async Task DisposeAsync()
    {
        if (_keysToCleanup.Count > 0)
            await _db.KeyDeleteAsync(_keysToCleanup.Select(k => (RedisKey)k).ToArray());

        await _redis.DisposeAsync();
    }

    private static long UniqueChannel() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);

    private static string CountKey(long channelId) => $"channel:{channelId}:count";

    private static string MarkKey(long userId, long channelId) => $"unread:mark:{userId}:{channelId}";

    // -------------------------------------------------------------------------
    // Guild IncrementForChannelAsync — O(1): channel counter + one ChannelActivity ping
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GuildIncrement_ShouldIncrementChannelCounter_AndPingGuild_NoPerUserUpdate()
    {
        var channelId = UniqueChannel();
        _keysToCleanup.Add(CountKey(channelId));

        await _sut.IncrementForChannelAsync(guildId: 7, channelId: channelId, senderUserId: 99);
        await _sut.IncrementForChannelAsync(guildId: 7, channelId: channelId, senderUserId: 99);

        // One durable counter for the whole channel — not per user.
        (await _db.StringGetAsync(CountKey(channelId))).ToString().Should().Be("2");

        // One guild-group ping per message, carrying the sender as author. Ids only.
        _broadcaster.Verify(
            b => b.BroadcastChannelActivityAsync(
                It.Is<ChannelActivityPayload>(p =>
                    p.ChannelId == channelId && p.GuildId == 7 && p.AuthorId == 99),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // The per-recipient fan-out is gone entirely on the guild path.
        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(
                It.IsAny<long>(), It.IsAny<UnreadCountPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -------------------------------------------------------------------------
    // DM IncrementForChannelAsync — the surviving bounded per-participant path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DmIncrement_ShouldIncrementEachParticipant_ExceptSender_WithAbsoluteBroadcast()
    {
        var channelId = UniqueChannel();
        const long sender = 99;
        _dms.Setup(d => d.GetParticipantIdsAsync(channelId))
            .ReturnsAsync(new List<long> { sender, 1, 2 });
        foreach (var uid in new[] { sender, 1L, 2L })
            _keysToCleanup.Add(RedisUnreadCountService.UnreadKey(uid, channelId));

        await _sut.IncrementForChannelAsync(guildId: null, channelId: channelId, senderUserId: sender);

        foreach (var uid in new[] { 1L, 2L })
        {
            (await _db.StringGetAsync(RedisUnreadCountService.UnreadKey(uid, channelId)))
                .ToString().Should().Be("1", $"user {uid} is a DM recipient");
            _broadcaster.Verify(
                b => b.BroadcastUnreadCountAsync(
                    uid,
                    It.Is<UnreadCountPayload>(p =>
                        p.ChannelId == channelId && p.GuildId == null && p.UnreadCount == 1),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        (await _db.KeyExistsAsync(RedisUnreadCountService.UnreadKey(sender, channelId)))
            .Should().BeFalse("sender is excluded");
    }

    // -------------------------------------------------------------------------
    // MarkReadAsync — truth-first, then guild anchor / DM key-clear, then zero broadcast
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MarkRead_GuildChannel_ShouldAnchorMarkToCurrentCount_AndBroadcastZero()
    {
        var channelId = UniqueChannel();
        _keysToCleanup.Add(CountKey(channelId));
        _keysToCleanup.Add(MarkKey(5, channelId));

        // Channel has seen 7 messages.
        await _db.StringSetAsync(CountKey(channelId), 7);

        await _sut.MarkReadAsync(userId: 5, guildId: 2, channelId: channelId, lastReadMessageId: 9000);

        _readStates.Verify(
            r => r.MarkAsReadAsync(5, channelId, 9000, It.IsAny<CancellationToken>()), Times.Once);

        // The mark is anchored to the current channel count → unread (count − mark) is 0 now.
        (await _db.StringGetAsync(MarkKey(5, channelId))).ToString().Should().Be("7");

        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(
                5,
                It.Is<UnreadCountPayload>(p => p.ChannelId == channelId && p.UnreadCount == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkRead_Dm_ShouldClearPerUserKey_AndBroadcastZero()
    {
        var channelId = UniqueChannel();
        var key = RedisUnreadCountService.UnreadKey(5, channelId);
        _keysToCleanup.Add(key);
        await _db.StringSetAsync(key, "7");

        await _sut.MarkReadAsync(userId: 5, guildId: null, channelId: channelId, lastReadMessageId: 9000);

        (await _db.KeyExistsAsync(key)).Should().BeFalse("DM mark-as-read deletes the per-user key");
        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(
                5,
                It.Is<UnreadCountPayload>(p => p.ChannelId == channelId && p.UnreadCount == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

        _broadcaster.Verify(
            b => b.BroadcastUnreadCountAsync(It.IsAny<long>(), It.IsAny<UnreadCountPayload>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no zero-broadcast when the truth write never succeeded");
    }

    // -------------------------------------------------------------------------
    // GetUnreadForUserAsync — count − mark, ViewChannel filter, lazy anchor
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetUnread_ShouldReturnCountMinusMark_OnlyWhenPositive()
    {
        var ch1 = UniqueChannel(); // count 5, mark 2 → 3
        var ch2 = UniqueChannel(); // count 4, mark 4 → 0 (excluded)
        const long userId = 42;
        _keysToCleanup.AddRange([CountKey(ch1), CountKey(ch2), MarkKey(userId, ch1), MarkKey(userId, ch2)]);

        await _db.StringSetAsync(CountKey(ch1), 5);
        await _db.StringSetAsync(MarkKey(userId, ch1), 2);
        await _db.StringSetAsync(CountKey(ch2), 4);
        await _db.StringSetAsync(MarkKey(userId, ch2), 4);

        var result = await _sut.GetUnreadForUserAsync(
            userId, new Dictionary<long, long> { [ch1] = 1, [ch2] = 1 });

        result.Should().HaveCount(1);
        result[ch1].Should().Be(3);
        result.Should().NotContainKey(ch2, "count == mark means fully read");
    }

    [Fact]
    public async Task GetUnread_ShouldDropChannels_TheUserCannotView()
    {
        var visible = UniqueChannel(); // count 5, mark 0 → 5
        var hidden = UniqueChannel(); // count 9, mark 0, but not viewable
        const long userId = 42;
        _keysToCleanup.AddRange([CountKey(visible), CountKey(hidden), MarkKey(userId, visible), MarkKey(userId, hidden)]);

        await _db.StringSetAsync(CountKey(visible), 5);
        await _db.StringSetAsync(MarkKey(userId, visible), 0);
        await _db.StringSetAsync(CountKey(hidden), 9);
        await _db.StringSetAsync(MarkKey(userId, hidden), 0);

        _permissions
            .Setup(p => p.HasAsync(userId, It.IsAny<long>(), It.IsAny<Harmony.Domain.Domain.Enums.Permission>(), hidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.GetUnreadForUserAsync(
            userId, new Dictionary<long, long> { [visible] = 1, [hidden] = 1 });

        result.Should().ContainKey(visible).WhoseValue.Should().Be(5);
        result.Should().NotContainKey(hidden, "override-hidden channels never accrue a badge");
    }

    [Fact]
    public async Task GetUnread_NoMark_ShouldReturnCaughtUp_AndLazilyAnchorTheMark()
    {
        var channelId = UniqueChannel(); // count 4, no mark → 0 now, mark lazily anchored to 4
        const long userId = 42;
        _keysToCleanup.AddRange([CountKey(channelId), MarkKey(userId, channelId)]);
        await _db.StringSetAsync(CountKey(channelId), 4);

        var result = await _sut.GetUnreadForUserAsync(
            userId, new Dictionary<long, long> { [channelId] = 1 });

        result.Should().NotContainKey(channelId, "a channel with no mark is caught-up");
        (await _db.StringGetAsync(MarkKey(userId, channelId))).ToString()
            .Should().Be("4", "the mark is lazily anchored to the current count on first read");
    }

    [Fact]
    public async Task GetUnread_WithNoChannels_ShouldReturnEmpty()
    {
        var result = await _sut.GetUnreadForUserAsync(
            userId: 1, channelGuildMap: new Dictionary<long, long>());
        result.Should().BeEmpty();
    }
}
