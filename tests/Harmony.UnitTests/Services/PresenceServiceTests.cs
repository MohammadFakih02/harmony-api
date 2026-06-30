using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Harmony.UnitTests.Services;

public class PresenceServiceTests
{
    private static (
        RedisPresenceService sut,
        Mock<IDatabase> db,
        Mock<IHubBroadcaster> broadcaster
    ) BuildSut(bool redisConnected = true)
    {
        var dbMock = new Mock<IDatabase>();
        var multiplexerMock = new Mock<IConnectionMultiplexer>();
        multiplexerMock.Setup(m => m.IsConnected).Returns(redisConnected);
        multiplexerMock
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(dbMock.Object);

        var providerMock = new Mock<IRedisConnectionProvider>();
        providerMock.Setup(p => p.Connection).Returns(redisConnected ? multiplexerMock.Object : null);
        providerMock.Setup(p => p.IsConnected).Returns(redisConnected);

        var broadcaster = new Mock<IHubBroadcaster>();
        var users = new Mock<IUserRepository>(); // GetByIdAsync → null ⇒ preferred defaults to "online"
        var friends = new Mock<IFriendRepository>(); // no friends ⇒ broadcasts reach no recipients
        friends.Setup(f => f.GetFriendIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
        var guilds = new Mock<IGuildRepository>(); // no shared guilds ⇒ guild fan-out reaches no groups
        guilds.Setup(g => g.GetGuildIdsForUserAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());

        var sut = new RedisPresenceService(
            providerMock.Object,
            broadcaster.Object,
            users.Object,
            friends.Object,
            guilds.Object,
            NullLogger<RedisPresenceService>.Instance
        );

        return (sut, dbMock, broadcaster);
    }

    private static void SetupSessionCount(Mock<IDatabase> db, long userId, long count) =>
        db.Setup(d =>
                d.SetLengthAsync(
                    It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.SessionKey(userId)),
                    It.IsAny<CommandFlags>()
                )
            )
            .ReturnsAsync(count);

    /// <summary>Stubs the cached preferred status so GetPreferred is a cache hit (no Postgres).</summary>
    private static void SetupPreferred(Mock<IDatabase> db, long userId, string preferred) =>
        db.Setup(d =>
                d.StringGetAsync(
                    It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.PreferredKey(userId)),
                    It.IsAny<CommandFlags>()
                )
            )
            .ReturnsAsync((RedisValue)preferred);

    /// <summary>Stubs the idle flag presence for the user.</summary>
    private static void SetupIdle(Mock<IDatabase> db, long userId, bool idle) =>
        db.Setup(d =>
                d.KeyExistsAsync(
                    It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.IdleKey(userId)),
                    It.IsAny<CommandFlags>()
                )
            )
            .ReturnsAsync(idle);

    [Fact]
    public async Task SetOnlineAsync_WhenRedisDown_DoesNotThrowOrBroadcast()
    {
        var (sut, _, broadcaster) = BuildSut(redisConnected: false);

        var act = () => sut.SetOnlineAsync(userId: 1, connectionId: "conn-1");

        await act.Should().NotThrowAsync();
        broadcaster.Verify(
            b => b.BroadcastOnlineStatusAsync(
                It.IsAny<long>(),
                It.IsAny<OnlineStatusPayload>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task SetOnlineAsync_FirstConnection_TracksSessionAndStatus()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 1);

        await sut.SetOnlineAsync(userId: 1, connectionId: "conn-1");

        db.Verify(
            d => d.SetAddAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.SessionKey(1)),
                It.Is<RedisValue>(v => v.ToString() == "conn-1"),
                It.IsAny<CommandFlags>()
            ),
            Times.Once
        );
        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.StatusKey(1)),
                It.Is<RedisValue>(v => v.ToString() == "online"),
                It.IsAny<Expiration>()
            ),
            Times.Once
        );

        // No friends-system yet — recipient resolution returns no one, so nothing to send to.
        broadcaster.Verify(
            b => b.BroadcastOnlineStatusAsync(
                It.IsAny<long>(),
                It.IsAny<OnlineStatusPayload>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task SetOnlineAsync_AdditionalConnection_DoesNotReBroadcast()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 2); // already had a tab open

        await sut.SetOnlineAsync(userId: 1, connectionId: "conn-2");

        broadcaster.Verify(
            b => b.BroadcastOnlineStatusAsync(
                It.IsAny<long>(),
                It.IsAny<OnlineStatusPayload>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task SetOfflineAsync_WhenRedisDown_DoesNotThrowOrBroadcast()
    {
        var (sut, _, broadcaster) = BuildSut(redisConnected: false);

        var act = () => sut.SetOfflineAsync(userId: 1, connectionId: "conn-1");

        await act.Should().NotThrowAsync();
        broadcaster.Verify(
            b => b.BroadcastOfflineStatusAsync(
                It.IsAny<long>(),
                It.IsAny<OfflineStatusPayload>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task SetOfflineAsync_LastConnection_ClearsStatusKey()
    {
        var (sut, db, _) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 0); // last tab just closed

        await sut.SetOfflineAsync(userId: 1, connectionId: "conn-1");

        db.Verify(
            d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.StatusKey(1)),
                It.IsAny<CommandFlags>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task SetOfflineAsync_OtherConnectionsRemain_KeepsStatusKey()
    {
        var (sut, db, _) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 1); // another tab still open

        await sut.SetOfflineAsync(userId: 1, connectionId: "conn-2");

        db.Verify(
            d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()),
            Times.Never
        );
    }

    [Fact]
    public async Task HeartbeatAsync_WhenRedisDown_DoesNotThrow()
    {
        var (sut, _, _) = BuildSut(redisConnected: false);

        var act = () => sut.HeartbeatAsync(userId: 1);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HeartbeatAsync_RefreshesStatusTtl()
    {
        var (sut, db, _) = BuildSut();

        await sut.HeartbeatAsync(userId: 1);

        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.StatusKey(1)),
                It.Is<RedisValue>(v => v.ToString() == "online"),
                It.IsAny<Expiration>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetStatusAsync_WhenRedisDown_ReturnsOffline()
    {
        var (sut, _, _) = BuildSut(redisConnected: false);

        var status = await sut.GetStatusAsync(userId: 1);

        status.Should().Be("offline");
    }

    [Fact]
    public async Task GetStatusAsync_WhenKeyAbsent_ReturnsOffline()
    {
        var (sut, db, _) = BuildSut();
        db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var status = await sut.GetStatusAsync(userId: 1);

        status.Should().Be("offline");
    }

    [Fact]
    public async Task GetStatusAsync_WhenKeyPresent_ReturnsStoredValue()
    {
        var (sut, db, _) = BuildSut();
        db.Setup(d =>
                d.StringGetAsync(
                    It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.StatusKey(1)),
                    It.IsAny<CommandFlags>()
                )
            )
            .ReturnsAsync((RedisValue)"online");

        var status = await sut.GetStatusAsync(userId: 1);

        status.Should().Be("online");
    }

    // -------------------------------------------------------------------------
    // Effective resolution — invisible suppression, manual status, auto-away
    // -------------------------------------------------------------------------

    private static void VerifyStatusKeyWritten(Mock<IDatabase> db, long userId, string effective) =>
        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.StatusKey(userId)),
                It.Is<RedisValue>(v => v.ToString() == effective),
                It.IsAny<Expiration>()
            ),
            Times.Once
        );

    [Fact]
    public async Task SetOnline_InvisibleFirstConnection_StoresOffline_AndSuppressesOnlineBroadcast()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 1);
        SetupPreferred(db, userId: 1, "invisible");

        await sut.SetOnlineAsync(userId: 1, connectionId: "conn-1");

        VerifyStatusKeyWritten(db, 1, "offline"); // invisible resolves to offline publicly
        broadcaster.Verify(
            b => b.BroadcastOnlineStatusAsync(
                It.IsAny<long>(),
                It.IsAny<OnlineStatusPayload>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never,
            "an invisible user must not be revealed coming online"
        );
    }

    [Fact]
    public async Task SetPreferredStatus_WhenConnected_WritesEffectiveStatus_AndSelfBroadcastsPreferred()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 1); // connected
        SetupIdle(db, userId: 1, idle: false);

        await sut.SetPreferredStatusAsync(userId: 1, preferred: "invisible");

        // Cache the preferred value...
        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.PreferredKey(1)),
                It.Is<RedisValue>(v => v.ToString() == "invisible")
            ),
            Times.Once
        );
        // ...write the masked effective status others see...
        VerifyStatusKeyWritten(db, 1, "offline");
        // ...and the user's own tabs receive the REAL preferred value (self broadcast).
        broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                1,
                It.Is<StatusChangedPayload>(p => p.UserId == 1 && p.Status == "invisible"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task SetPreferredStatus_WhenNotConnected_UpdatesCacheOnly_NoStatusKeyNoBroadcast()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 0); // not connected

        await sut.SetPreferredStatusAsync(userId: 1, preferred: "dnd");

        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.PreferredKey(1)),
                It.Is<RedisValue>(v => v.ToString() == "dnd")
            ),
            Times.Once
        );
        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.StatusKey(1)),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>()
            ),
            Times.Never,
            "no public status key for a disconnected user"
        );
        broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                It.IsAny<long>(),
                It.IsAny<StatusChangedPayload>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task SetIdle_True_WhenOnlineAndConnected_FlipsToAway_AndBroadcasts()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 1);
        SetupPreferred(db, userId: 1, "online");

        await sut.SetIdleAsync(userId: 1, idle: true);

        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.IdleKey(1)),
                It.Is<RedisValue>(v => v.ToString() == "1")
            ),
            Times.Once
        );
        VerifyStatusKeyWritten(db, 1, "away");
        broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                1,
                It.Is<StatusChangedPayload>(p => p.Status == "away"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task SetIdle_True_WhenPreferredDnd_StoresFlagButDoesNotChangeStatus()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 1);
        SetupPreferred(db, userId: 1, "dnd");

        await sut.SetIdleAsync(userId: 1, idle: true);

        // Flag is recorded, but a manual dnd is not overridden by idle.
        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.IdleKey(1)),
                It.Is<RedisValue>(v => v.ToString() == "1")
            ),
            Times.Once
        );
        db.Verify(
            d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.StatusKey(1)),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>()
            ),
            Times.Never
        );
        broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                It.IsAny<long>(),
                It.IsAny<StatusChangedPayload>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    [Fact]
    public async Task SetIdle_False_ClearsFlag_AndReturnsToOnline()
    {
        var (sut, db, broadcaster) = BuildSut();
        SetupSessionCount(db, userId: 1, count: 1);
        SetupPreferred(db, userId: 1, "online");

        await sut.SetIdleAsync(userId: 1, idle: false);

        db.Verify(
            d => d.KeyDeleteAsync(
                It.Is<RedisKey>(k => k.ToString() == RedisPresenceService.IdleKey(1)),
                It.IsAny<CommandFlags>()
            ),
            Times.Once
        );
        VerifyStatusKeyWritten(db, 1, "online");
    }

    [Fact]
    public async Task Heartbeat_PreservesManualAway_DoesNotResetToOnline()
    {
        var (sut, db, _) = BuildSut();
        SetupPreferred(db, userId: 1, "away");

        await sut.HeartbeatAsync(userId: 1);

        VerifyStatusKeyWritten(db, 1, "away");
    }

    // -------------------------------------------------------------------------
    // Fail-open on the new methods
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetPreferredStatus_WhenRedisDown_DoesNotThrow()
    {
        var (sut, _, _) = BuildSut(redisConnected: false);
        var act = () => sut.SetPreferredStatusAsync(userId: 1, preferred: "dnd");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetIdle_WhenRedisDown_DoesNotThrow()
    {
        var (sut, _, _) = BuildSut(redisConnected: false);
        var act = () => sut.SetIdleAsync(userId: 1, idle: true);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetStatuses_WhenRedisDown_ReturnsOfflineForAll()
    {
        var (sut, _, _) = BuildSut(redisConnected: false);

        var result = await sut.GetStatusesAsync([1, 2, 3]);

        result.Should().HaveCount(3);
        result.Values.Should().OnlyContain(v => v == "offline");
    }

    [Fact]
    public async Task GetPreferredStatus_WhenRedisDown_ReturnsOnline()
    {
        var (sut, _, _) = BuildSut(redisConnected: false);
        (await sut.GetPreferredStatusAsync(userId: 1)).Should().Be("online");
    }
}
