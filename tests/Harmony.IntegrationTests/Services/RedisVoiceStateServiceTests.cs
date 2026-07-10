using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Harmony.IntegrationTests.Services;

/// <summary>
/// Integration tests for <see cref="RedisVoiceStateService"/> against real Redis. Isolates the
/// service's own guarantees against live HASH/SET/pointer state: join publishes a participant,
/// joining a second room evicts the first, leave clears, state updates mutate + broadcast, and the
/// ghost sweep reaps a participant whose presence status key is absent (offline). The broadcaster is
/// mocked — these assert Redis state + broadcast intent, not delivery.
///
/// Requires Redis on localhost:6379. Keys are cleaned up after each test.
/// </summary>
public class RedisVoiceStateServiceTests : IAsyncLifetime
{
    private IConnectionMultiplexer _redis = null!;
    private IDatabase _db = null!;
    private Mock<IHubBroadcaster> _broadcaster = null!;
    private RedisVoiceStateService _sut = null!;

    private readonly List<long> _userIds = [];
    private readonly List<long> _channelIds = [];

    public async Task InitializeAsync()
    {
        var options = ConfigurationOptions.Parse("localhost:6379,abortConnect=false");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        _db = _redis.GetDatabase();

        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock.Setup(p => p.Connection).Returns(_redis);
        providerMock.Setup(p => p.IsConnected).Returns(true);

        _broadcaster = new Mock<IHubBroadcaster>();

        _sut = new RedisVoiceStateService(
            providerMock.Object,
            _broadcaster.Object,
            NullLogger<RedisVoiceStateService>.Instance
        );
    }

    public async Task DisposeAsync()
    {
        foreach (var channelId in _channelIds)
            await _db.KeyDeleteAsync($"voice:channel:{channelId}");
        foreach (var userId in _userIds)
        {
            await _db.KeyDeleteAsync($"voice:user:{userId}");
            await _db.SetRemoveAsync("voice:users", userId.ToString());
            await _db.KeyDeleteAsync(RedisPresenceService.StatusKey(userId));
        }

        await _redis.DisposeAsync();
    }

    private long UniqueId()
    {
        var id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
        return id;
    }

    private long TrackUser()
    {
        var id = UniqueId();
        _userIds.Add(id);
        return id;
    }

    private long TrackChannel()
    {
        var id = UniqueId();
        _channelIds.Add(id);
        return id;
    }

    // -------------------------------------------------------------------------

    [Fact]
    public async Task Join_PublishesParticipant_AndBroadcasts()
    {
        var channelId = TrackChannel();
        var guildId = UniqueId();
        var userId = TrackUser();

        await _sut.JoinAsync(channelId, guildId, userId);

        (await _db.HashExistsAsync($"voice:channel:{channelId}", userId.ToString()))
            .Should()
            .BeTrue();
        (await _db.StringGetAsync($"voice:user:{userId}")).ToString().Should().Be(channelId.ToString());
        (await _db.SetContainsAsync("voice:users", userId.ToString())).Should().BeTrue();

        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceParticipantJoinedAsync(
                    It.Is<VoiceParticipantPayload>(p =>
                        p.ChannelId == channelId && p.GuildId == guildId && p.UserId == userId
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Join_SecondRoom_EvictsFirst()
    {
        var roomA = TrackChannel();
        var roomB = TrackChannel();
        var userId = TrackUser();

        await _sut.JoinAsync(roomA, guildId: null, userId);
        await _sut.JoinAsync(roomB, guildId: null, userId);

        (await _db.HashExistsAsync($"voice:channel:{roomA}", userId.ToString()))
            .Should()
            .BeFalse("joining a new room evicts the old one");
        (await _db.HashExistsAsync($"voice:channel:{roomB}", userId.ToString())).Should().BeTrue();
        (await _db.StringGetAsync($"voice:user:{userId}")).ToString().Should().Be(roomB.ToString());

        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceParticipantLeftAsync(
                    It.Is<VoiceParticipantLeftPayload>(p => p.ChannelId == roomA && p.UserId == userId),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Leave_ClearsState_AndBroadcasts()
    {
        var channelId = TrackChannel();
        var userId = TrackUser();

        await _sut.JoinAsync(channelId, guildId: null, userId);
        await _sut.LeaveAsync(userId);

        (await _db.HashExistsAsync($"voice:channel:{channelId}", userId.ToString()))
            .Should()
            .BeFalse();
        (await _db.KeyExistsAsync($"voice:user:{userId}")).Should().BeFalse();
        (await _db.SetContainsAsync("voice:users", userId.ToString())).Should().BeFalse();

        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceParticipantLeftAsync(
                    It.Is<VoiceParticipantLeftPayload>(p =>
                        p.ChannelId == channelId && p.UserId == userId
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task UpdateState_MutatesFlags_AndBroadcasts()
    {
        var channelId = TrackChannel();
        var userId = TrackUser();

        await _sut.JoinAsync(channelId, guildId: null, userId);
        await _sut.UpdateStateAsync(userId, isMuted: true, isDeafened: false, isVideoOn: true, isStreaming: false);

        var participants = await _sut.GetChannelParticipantsAsync(channelId);
        var me = participants.Single(p => p.UserId == userId);
        me.IsMuted.Should().BeTrue();
        me.IsVideoOn.Should().BeTrue();
        me.IsDeafened.Should().BeFalse();

        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceStateUpdatedAsync(
                    It.Is<VoiceParticipantPayload>(p =>
                        p.UserId == userId && p.IsMuted && p.IsVideoOn
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetCurrentRoom_ReturnsRoomAfterJoin_AndNullAfterLeave()
    {
        var channelId = TrackChannel();
        var guildId = UniqueId();
        var userId = TrackUser();

        (await _sut.GetCurrentRoomAsync(userId)).Should().BeNull("not in any room yet");

        await _sut.JoinAsync(channelId, guildId, userId);
        var room = await _sut.GetCurrentRoomAsync(userId);
        room.Should().NotBeNull();
        room!.Value.ChannelId.Should().Be(channelId);
        room.Value.GuildId.Should().Be(guildId);

        await _sut.LeaveAsync(userId);
        (await _sut.GetCurrentRoomAsync(userId)).Should().BeNull("left the room");
    }

    [Fact]
    public async Task GetCurrentRoom_DmRoom_HasNullGuildId()
    {
        var channelId = TrackChannel();
        var userId = TrackUser();

        await _sut.JoinAsync(channelId, guildId: null, userId);

        var room = await _sut.GetCurrentRoomAsync(userId);
        room.Should().NotBeNull();
        room!.Value.ChannelId.Should().Be(channelId);
        room.Value.GuildId.Should().BeNull("a DM/group-DM call has no guild");
    }

    [Fact]
    public async Task SweepGhosts_ReapsOfflineParticipant_KeepsConnectedOne()
    {
        var channelId = TrackChannel();
        var ghost = TrackUser();
        var live = TrackUser();

        await _sut.JoinAsync(channelId, guildId: null, ghost);
        await _sut.JoinAsync(channelId, guildId: null, live);

        // The "live" user has a presence status key (heartbeat-kept); the "ghost" does not.
        await _db.StringSetAsync(RedisPresenceService.StatusKey(live), "online");

        var reaped = await _sut.SweepGhostsAsync();

        reaped.Should().BeGreaterThanOrEqualTo(1);
        (await _db.HashExistsAsync($"voice:channel:{channelId}", ghost.ToString()))
            .Should()
            .BeFalse("the offline ghost is reaped");
        (await _db.HashExistsAsync($"voice:channel:{channelId}", live.ToString()))
            .Should()
            .BeTrue("the connected participant is kept");
    }
}
