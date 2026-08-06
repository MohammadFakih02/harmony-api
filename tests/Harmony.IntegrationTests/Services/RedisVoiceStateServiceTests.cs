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
    private readonly List<long> _guildIds = [];

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
        {
            await _db.KeyDeleteAsync($"voice:channel:{channelId}");
            await _db.KeyDeleteAsync($"call:ring:{channelId}");
        }
        foreach (var userId in _userIds)
        {
            await _db.KeyDeleteAsync($"voice:user:{userId}");
            await _db.SetRemoveAsync("voice:users", userId.ToString());
            await _db.KeyDeleteAsync(RedisPresenceService.StatusKey(userId));
        }
        foreach (var guildId in _guildIds)
            await _db.KeyDeleteAsync($"voice:moderation:{guildId}");

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

    private long TrackGuild()
    {
        var id = UniqueId();
        _guildIds.Add(id);
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
    public async Task TryBeginRing_SetsCallerWithTtl_AndRejectsASecondRing()
    {
        var channelId = TrackChannel();
        var caller = TrackUser();
        var other = TrackUser();

        (await _sut.TryBeginRingAsync(channelId, caller)).Should().BeTrue();

        var raw = await _db.StringGetAsync($"call:ring:{channelId}");
        raw.ToString().Should().Be(caller.ToString());
        (await _db.KeyTimeToLiveAsync($"call:ring:{channelId}"))
            .Should()
            .NotBeNull("the ring key must TTL out as the caller-crash backstop");

        (await _sut.TryBeginRingAsync(channelId, other))
            .Should()
            .BeFalse("SET NX rejects a second ring while one is live");
        (await _sut.GetRingCallerAsync(channelId)).Should().Be(caller, "the original ring survives");
    }

    [Fact]
    public async Task TryEndRing_TrueOnlyWhileLive()
    {
        var channelId = TrackChannel();
        var caller = TrackUser();

        (await _sut.TryEndRingAsync(channelId)).Should().BeFalse("no ring to end yet");

        await _sut.TryBeginRingAsync(channelId, caller);
        (await _sut.TryEndRingAsync(channelId)).Should().BeTrue("a live ring existed");
        (await _sut.TryEndRingAsync(channelId)).Should().BeFalse("the key is already gone");
        (await _sut.GetRingCallerAsync(channelId)).Should().BeNull();
    }

    [Fact]
    public async Task GetRingCaller_ReturnsCaller_AndNullWhenNoRing()
    {
        var channelId = TrackChannel();
        var caller = TrackUser();

        (await _sut.GetRingCallerAsync(channelId)).Should().BeNull();

        await _sut.TryBeginRingAsync(channelId, caller);
        (await _sut.GetRingCallerAsync(channelId)).Should().Be(caller);
    }

    [Fact]
    public async Task Moderate_SetsServerFlags_AndBroadcasts()
    {
        var channelId = TrackChannel();
        var guildId = TrackGuild();
        var target = TrackUser();

        await _sut.JoinAsync(channelId, guildId, target);
        var applied = await _sut.ModerateAsync(channelId, target, serverMute: true, serverDeafen: null);

        applied.Should().BeTrue();
        var me = (await _sut.GetChannelParticipantsAsync(channelId)).Single(p => p.UserId == target);
        me.IsServerMuted.Should().BeTrue();
        me.IsServerDeafened.Should().BeFalse("only the mute flag was set");
        me.IsMuted.Should().BeFalse("server flags are orthogonal to the self flags");

        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceStateUpdatedAsync(
                    It.Is<VoiceParticipantPayload>(p => p.UserId == target && p.IsServerMuted),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Moderate_ReturnsFalse_WhenTargetNotInThatRoom()
    {
        var channelId = TrackChannel();
        var otherRoom = TrackChannel();
        var guildId = TrackGuild();
        var target = TrackUser();

        (await _sut.ModerateAsync(channelId, target, true, null))
            .Should()
            .BeFalse("target is in no room at all");

        await _sut.JoinAsync(otherRoom, guildId, target);
        (await _sut.ModerateAsync(channelId, target, true, null))
            .Should()
            .BeFalse("target is in a different room");
    }

    [Fact]
    public async Task Leave_WhenRoomEntryAlreadyGone_DoesNotBroadcast_AStaleLeave()
    {
        // Reproduces the loser's view of a concurrent leave (an explicit LeaveVoice racing the
        // OnDisconnected teardown): voice:user still points at the room, but the winner already
        // removed the room-HASH entry. The loser must NOT emit a second leave — without the guard it
        // would broadcast a guildId-less leave that never reaches the guild sidebar (#1), leaving the
        // participant stuck on every other member's roster.
        var channelId = TrackChannel();
        var userId = TrackUser();

        // No HASH entry — only the dangling pointer the winner hasn't cleared yet.
        await _db.StringSetAsync($"voice:user:{userId}", channelId.ToString());

        await _sut.LeaveAsync(userId);

        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceParticipantLeftAsync(
                    It.IsAny<VoiceParticipantLeftPayload>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        // The dangling pointer is still cleaned up so the user isn't wedged "in" a room.
        (await _db.KeyExistsAsync($"voice:user:{userId}")).Should().BeFalse();
    }

    [Fact]
    public async Task Moderate_ServerFlags_AreSticky_AcrossRejoin_UntilCleared()
    {
        var channelId = TrackChannel();
        var guildId = TrackGuild();
        var target = TrackUser();

        await _sut.JoinAsync(channelId, guildId, target);
        await _sut.ModerateAsync(channelId, target, serverMute: true, serverDeafen: true);

        // Leave + rejoin must NOT shake the server flags off (the whole point of stickiness).
        await _sut.LeaveAsync(target);
        await _sut.JoinAsync(channelId, guildId, target);

        var rejoined = (await _sut.GetChannelParticipantsAsync(channelId)).Single(p =>
            p.UserId == target
        );
        rejoined.IsServerMuted.Should().BeTrue("sticky server mute survives a rejoin");
        rejoined.IsServerDeafened.Should().BeTrue("sticky server deafen survives a rejoin");

        // A moderator clearing both flags also clears the sticky entry.
        await _sut.ModerateAsync(channelId, target, serverMute: false, serverDeafen: false);
        (await _db.HashExistsAsync($"voice:moderation:{guildId}", target.ToString()))
            .Should()
            .BeFalse("fully un-moderated = no sticky entry");

        await _sut.LeaveAsync(target);
        await _sut.JoinAsync(channelId, guildId, target);
        var cleared = (await _sut.GetChannelParticipantsAsync(channelId)).Single(p =>
            p.UserId == target
        );
        cleared.IsServerMuted.Should().BeFalse();
        cleared.IsServerDeafened.Should().BeFalse();
    }

    [Fact]
    public async Task Move_CarriesAllFlags_AndBroadcastsLeavePlusJoin()
    {
        var roomA = TrackChannel();
        var roomB = TrackChannel();
        var guildId = TrackGuild();
        var target = TrackUser();

        await _sut.JoinAsync(roomA, guildId, target);
        await _sut.UpdateStateAsync(target, isMuted: true, isDeafened: false, isVideoOn: false, isStreaming: false);
        await _sut.ModerateAsync(roomA, target, serverMute: true, serverDeafen: null);

        (await _sut.MoveAsync(target, roomA, roomB, guildId)).Should().BeTrue();

        (await _db.HashExistsAsync($"voice:channel:{roomA}", target.ToString())).Should().BeFalse();
        (await _db.StringGetAsync($"voice:user:{target}")).ToString().Should().Be(roomB.ToString());

        var moved = (await _sut.GetChannelParticipantsAsync(roomB)).Single(p => p.UserId == target);
        moved.IsMuted.Should().BeTrue("self mute travels with the move");
        moved.IsServerMuted.Should().BeTrue("server mute travels with the move");

        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceParticipantLeftAsync(
                    It.Is<VoiceParticipantLeftPayload>(p => p.ChannelId == roomA && p.UserId == target),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _broadcaster.Verify(
            b =>
                b.BroadcastVoiceParticipantJoinedAsync(
                    It.Is<VoiceParticipantPayload>(p =>
                        p.ChannelId == roomB && p.UserId == target && p.IsServerMuted
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Move_ReturnsFalse_WhenTargetNotInSourceRoom()
    {
        var roomA = TrackChannel();
        var roomB = TrackChannel();
        var guildId = TrackGuild();
        var target = TrackUser();

        (await _sut.MoveAsync(target, roomA, roomB, guildId))
            .Should()
            .BeFalse("target never joined the source room");
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
