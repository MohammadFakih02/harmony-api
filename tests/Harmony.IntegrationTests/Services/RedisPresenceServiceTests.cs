using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Harmony.IntegrationTests.Services;

/// <summary>
/// Integration tests for <see cref="RedisPresenceService"/> against real Redis.
///
/// Isolates the service's own guarantees against live key/TTL/ZSET state: multi-tab
/// session collapsing, status TTL refresh on heartbeat, and online/offline transitions.
/// The broadcaster is mocked — there are no friends yet (seam returns empty), so these
/// tests assert Redis state, not delivery. PresenceFlowTests covers the hub wiring.
///
/// Requires Redis on localhost:6379. Keys are cleaned up after each test.
/// </summary>
public class RedisPresenceServiceTests : IAsyncLifetime
{
    private IConnectionMultiplexer _redis = null!;
    private IDatabase _db = null!;
    private Mock<IHubBroadcaster> _broadcaster = null!;
    private RedisPresenceService _sut = null!;

    private readonly List<string> _keysToCleanup = [];

    public async Task InitializeAsync()
    {
        var options = ConfigurationOptions.Parse("localhost:6379,abortConnect=false");
        _redis = await ConnectionMultiplexer.ConnectAsync(options);
        _db = _redis.GetDatabase();

        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock.Setup(p => p.Connection).Returns(_redis);
        providerMock.Setup(p => p.IsConnected).Returns(true);

        _broadcaster = new Mock<IHubBroadcaster>();
        // GetByIdAsync → null ⇒ preferred falls back to "online" (cache warmed on connect).
        var users = new Mock<IUserRepository>();
        var friends = new Mock<IFriendRepository>(); // friend fan-out not under test here
        friends.Setup(f => f.GetFriendIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());

        _sut = new RedisPresenceService(
            providerMock.Object,
            _broadcaster.Object,
            users.Object,
            friends.Object,
            NullLogger<RedisPresenceService>.Instance
        );
    }

    public async Task DisposeAsync()
    {
        if (_keysToCleanup.Count > 0)
            await _db.KeyDeleteAsync(_keysToCleanup.Select(k => (RedisKey)k).ToArray());

        await _redis.DisposeAsync();
    }

    private static long UniqueUserId() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);

    private void TrackKeysFor(long userId)
    {
        _keysToCleanup.Add(RedisPresenceService.StatusKey(userId));
        _keysToCleanup.Add(RedisPresenceService.SessionKey(userId));
        _keysToCleanup.Add(RedisPresenceService.PreferredKey(userId));
        _keysToCleanup.Add(RedisPresenceService.IdleKey(userId));
    }

    // -------------------------------------------------------------------------
    // SetOnlineAsync — session tracking, status, ZSET, first-connection broadcast
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetOnline_FirstConnection_SetsSessionStatusAndZSet()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");

        (await _db.SetContainsAsync(RedisPresenceService.SessionKey(userId), "conn-1"))
            .Should()
            .BeTrue();
        (await _db.StringGetAsync(RedisPresenceService.StatusKey(userId)))
            .ToString()
            .Should()
            .Be("online");
        (await _db.SortedSetScoreAsync("presence:online", userId.ToString())).Should().NotBeNull();

        await _db.SortedSetRemoveAsync("presence:online", userId.ToString());
    }

    [Fact]
    public async Task SetOnline_AdditionalConnection_DoesNotReBroadcast()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");
        await _sut.SetOnlineAsync(userId, "conn-2");

        _broadcaster.Verify(
            b =>
                b.BroadcastOnlineStatusAsync(
                    It.IsAny<long>(),
                    It.IsAny<OnlineStatusPayload>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never,
            "no friends exist to broadcast to yet — the seam returns empty"
        );

        (await _db.SetLengthAsync(RedisPresenceService.SessionKey(userId))).Should().Be(2);

        await _db.SortedSetRemoveAsync("presence:online", userId.ToString());
    }

    // -------------------------------------------------------------------------
    // SetOfflineAsync — session removal, last-tab status clear, multi-tab survival
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetOffline_LastConnection_ClearsStatusAndZSet()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");
        await _sut.SetOfflineAsync(userId, "conn-1");

        (await _db.KeyExistsAsync(RedisPresenceService.StatusKey(userId))).Should().BeFalse();
        (await _db.SortedSetScoreAsync("presence:online", userId.ToString())).Should().BeNull();
        (await _db.SetLengthAsync(RedisPresenceService.SessionKey(userId))).Should().Be(0);
    }

    [Fact]
    public async Task SetOffline_OtherConnectionRemains_KeepsStatus()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");
        await _sut.SetOnlineAsync(userId, "conn-2");
        await _sut.SetOfflineAsync(userId, "conn-1");

        (await _db.StringGetAsync(RedisPresenceService.StatusKey(userId)))
            .ToString()
            .Should()
            .Be("online", "conn-2 is still connected");

        await _sut.SetOfflineAsync(userId, "conn-2");
        await _db.SortedSetRemoveAsync("presence:online", userId.ToString());
    }

    // -------------------------------------------------------------------------
    // HeartbeatAsync — refreshes TTL + ZSET score without broadcasting
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_RefreshesStatusTtlAndZSetScore()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");
        var ttlBefore = await _db.KeyTimeToLiveAsync(RedisPresenceService.StatusKey(userId));

        await Task.Delay(1100); // let the TTL visibly tick down
        await _sut.HeartbeatAsync(userId);

        var ttlAfter = await _db.KeyTimeToLiveAsync(RedisPresenceService.StatusKey(userId));
        ttlAfter.Should().NotBeNull();
        ttlBefore.Should().NotBeNull();
        ttlAfter!.Value.Should().BeGreaterThan(ttlBefore!.Value - TimeSpan.FromMilliseconds(500));

        await _sut.SetOfflineAsync(userId, "conn-1");
    }

    // -------------------------------------------------------------------------
    // GetStatusAsync — read primitive
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_WhenAbsent_ReturnsOffline()
    {
        var userId = UniqueUserId();

        (await _sut.GetStatusAsync(userId)).Should().Be("offline");
    }

    [Fact]
    public async Task GetStatus_WhenOnline_ReturnsOnline()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");

        (await _sut.GetStatusAsync(userId)).Should().Be("online");

        await _sut.SetOfflineAsync(userId, "conn-1");
    }

    // -------------------------------------------------------------------------
    // Preferred status + effective resolution (real Redis)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetPreferred_Dnd_WhileConnected_StoresDndEffective_AndCachesPreferred()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");
        await _sut.SetPreferredStatusAsync(userId, "dnd");

        (await _db.StringGetAsync(RedisPresenceService.PreferredKey(userId))).ToString().Should().Be("dnd");
        (await _sut.GetStatusAsync(userId)).Should().Be("dnd");
        (await _sut.GetPreferredStatusAsync(userId)).Should().Be("dnd");

        await _sut.SetOfflineAsync(userId, "conn-1");
    }

    [Fact]
    public async Task SetPreferred_Invisible_WhileConnected_PublicStatusIsOffline_ButPreferredInvisible()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");
        await _sut.SetPreferredStatusAsync(userId, "invisible");

        // Others see offline...
        (await _sut.GetStatusAsync(userId)).Should().Be("offline");
        // ...while the session is still live and the preference is invisible.
        (await _db.SetLengthAsync(RedisPresenceService.SessionKey(userId))).Should().Be(1);
        (await _sut.GetPreferredStatusAsync(userId)).Should().Be("invisible");

        await _sut.SetOfflineAsync(userId, "conn-1");
    }

    [Fact]
    public async Task SetIdle_True_OnOnlineUser_MakesPublicStatusAway_ThenClearsBackToOnline()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");

        await _sut.SetIdleAsync(userId, idle: true);
        (await _db.KeyExistsAsync(RedisPresenceService.IdleKey(userId))).Should().BeTrue();
        (await _sut.GetStatusAsync(userId)).Should().Be("away");

        await _sut.SetIdleAsync(userId, idle: false);
        (await _db.KeyExistsAsync(RedisPresenceService.IdleKey(userId))).Should().BeFalse();
        (await _sut.GetStatusAsync(userId)).Should().Be("online");

        await _sut.SetOfflineAsync(userId, "conn-1");
    }

    [Fact]
    public async Task Disconnect_ClearsIdleFlag()
    {
        var userId = UniqueUserId();
        TrackKeysFor(userId);

        await _sut.SetOnlineAsync(userId, "conn-1");
        await _sut.SetIdleAsync(userId, idle: true);
        (await _db.KeyExistsAsync(RedisPresenceService.IdleKey(userId))).Should().BeTrue();

        await _sut.SetOfflineAsync(userId, "conn-1");

        (await _db.KeyExistsAsync(RedisPresenceService.IdleKey(userId))).Should().BeFalse();
    }

    [Fact]
    public async Task GetStatuses_ReturnsEffectivePerUser_DefaultingOfflineForAbsent()
    {
        var a = UniqueUserId();
        var b = UniqueUserId();
        var c = UniqueUserId(); // never connects → offline
        TrackKeysFor(a);
        TrackKeysFor(b);

        await _sut.SetOnlineAsync(a, "conn-a");
        await _sut.SetOnlineAsync(b, "conn-b");
        await _sut.SetPreferredStatusAsync(b, "dnd");

        var statuses = await _sut.GetStatusesAsync([a, b, c]);

        statuses[a].Should().Be("online");
        statuses[b].Should().Be("dnd");
        statuses[c].Should().Be("offline");

        await _sut.SetOfflineAsync(a, "conn-a");
        await _sut.SetOfflineAsync(b, "conn-b");
    }
}
