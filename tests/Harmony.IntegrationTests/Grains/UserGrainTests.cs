using FluentAssertions;
using Harmony.API.Grains;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orleans.TestingHost;

namespace Harmony.IntegrationTests.Grains;

/// <summary>
/// D1 grain tests. Each test gets its own single-silo <see cref="InProcessTestCluster"/> (Orleans'
/// recommended in-process harness) with the grain's collaborators registered as Moq singletons —
/// the grain resolves the repositories through its <c>IServiceScopeFactory</c>, so registering them
/// as singletons is enough for a scope to hand them back. Fresh cluster + fresh mocks per test keeps
/// broadcast-count assertions isolated. This is independent of the WebApplicationFactory "Test"
/// hosts (where Orleans stays gated out) — it stands up its own auto-ported silo.
/// </summary>
public class UserGrainTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly Mock<IHubBroadcaster> _broadcaster = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IFriendRepository> _friends = new();
    private readonly Mock<IGuildRepository> _guilds = new();

    public async Task InitializeAsync()
    {
        // Defaults: unknown user ⇒ preferred "online"; no friends / no guilds ⇒ fan-out reaches nobody.
        // Individual tests override before acting.
        _users.Setup(u => u.GetByIdAsync(It.IsAny<long>())).ReturnsAsync((User?)null);
        _friends.Setup(f => f.GetFriendIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
        _guilds.Setup(g => g.GetGuildIdsForUserAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        builder.ConfigureSilo((_, siloBuilder) => siloBuilder.AddMemoryGrainStorage("Default"));
        builder.ConfigureHost(hostBuilder =>
        {
            hostBuilder.Services.AddSingleton(_broadcaster.Object);
            hostBuilder.Services.AddSingleton(_users.Object);
            hostBuilder.Services.AddSingleton(_friends.Object);
            hostBuilder.Services.AddSingleton(_guilds.Object);
        });

        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    private IUserGrain Grain(long id) => _cluster.Client.GetGrain<IUserGrain>(id);

    // -------------------------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetOnline_FirstConnection_BroadcastsOnlineToFriendsAndGuilds()
    {
        _friends.Setup(f => f.GetFriendIdsAsync(1)).ReturnsAsync(new List<long> { 2 });
        _guilds.Setup(g => g.GetGuildIdsForUserAsync(1)).ReturnsAsync(new List<long> { 10 });

        await Grain(1).SetOnline("c1");

        _broadcaster.Verify(
            b => b.BroadcastOnlineStatusAsync(
                2,
                It.Is<OnlineStatusPayload>(p => p.UserId == 1 && p.Status == "online"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        _broadcaster.Verify(
            b => b.BroadcastOnlineStatusToGuildAsync(10, It.IsAny<OnlineStatusPayload>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task SetOnline_SecondConnection_DoesNotRebroadcast()
    {
        _friends.Setup(f => f.GetFriendIdsAsync(1)).ReturnsAsync(new List<long> { 2 });

        await Grain(1).SetOnline("c1");
        await Grain(1).SetOnline("c2");

        _broadcaster.Verify(
            b => b.BroadcastOnlineStatusAsync(2, It.IsAny<OnlineStatusPayload>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task SetOnline_InvisibleUser_SuppressesBroadcast_ButCountsAsConnected()
    {
        _users.Setup(u => u.GetByIdAsync(3)).ReturnsAsync(new User { Id = 3, PreferredStatus = "invisible" });
        _friends.Setup(f => f.GetFriendIdsAsync(3)).ReturnsAsync(new List<long> { 2 });

        await Grain(3).SetOnline("c1");

        _broadcaster.Verify(
            b => b.BroadcastOnlineStatusAsync(It.IsAny<long>(), It.IsAny<OnlineStatusPayload>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        (await Grain(3).IsConnected()).Should().BeTrue();
        (await Grain(3).GetStatus()).Should().Be("offline"); // invisible masks to offline for observers
    }

    [Fact]
    public async Task SetOffline_LastConnection_BroadcastsOffline_AndStatusGoesOffline()
    {
        _friends.Setup(f => f.GetFriendIdsAsync(1)).ReturnsAsync(new List<long> { 2 });

        await Grain(1).SetOnline("c1");
        await Grain(1).SetOffline("c1");

        _broadcaster.Verify(
            b => b.BroadcastOfflineStatusAsync(
                2,
                It.Is<OfflineStatusPayload>(p => p.UserId == 1),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        (await Grain(1).GetStatus()).Should().Be("offline");
    }

    [Fact]
    public async Task SetOffline_WithOtherConnectionRemaining_StaysOnline()
    {
        _friends.Setup(f => f.GetFriendIdsAsync(1)).ReturnsAsync(new List<long> { 2 });

        await Grain(1).SetOnline("c1");
        await Grain(1).SetOnline("c2");
        await Grain(1).SetOffline("c1");

        _broadcaster.Verify(
            b => b.BroadcastOfflineStatusAsync(It.IsAny<long>(), It.IsAny<OfflineStatusPayload>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        (await Grain(1).IsConnected()).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Status reads / writes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_WhenDisconnected_ReturnsOffline_WithoutLoadingFromPostgres()
    {
        (await Grain(7).GetStatus()).Should().Be("offline");
        // The lazy-load optimisation: an offline user's grain never hits Postgres just to read "offline".
        _users.Verify(u => u.GetByIdAsync(7), Times.Never);
    }

    [Fact]
    public async Task SetPreferredStatus_Invisible_WhenConnected_FriendsSeeOffline_SelfSeesInvisible()
    {
        _friends.Setup(f => f.GetFriendIdsAsync(1)).ReturnsAsync(new List<long> { 2 });
        await Grain(1).SetOnline("c1");

        await Grain(1).SetPreferredStatus("invisible");

        _broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                2,
                It.Is<StatusChangedPayload>(p => p.Status == "offline"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        _broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                1,
                It.Is<StatusChangedPayload>(p => p.Status == "invisible"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        (await Grain(1).GetStatus()).Should().Be("offline");
        (await Grain(1).GetPreferredStatus()).Should().Be("invisible");
    }

    [Fact]
    public async Task SetPreferredStatus_WhenDisconnected_UpdatesCacheOnly_NoBroadcast()
    {
        await Grain(1).SetPreferredStatus("dnd");

        _broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(It.IsAny<long>(), It.IsAny<StatusChangedPayload>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        (await Grain(1).GetPreferredStatus()).Should().Be("dnd");
    }

    [Fact]
    public async Task SetIdle_WhenOnline_ShiftsEffectiveToAway_ForFriendsAndSelf()
    {
        _friends.Setup(f => f.GetFriendIdsAsync(1)).ReturnsAsync(new List<long> { 2 });
        await Grain(1).SetOnline("c1");

        await Grain(1).SetIdle(true);

        (await Grain(1).GetStatus()).Should().Be("away");
        _broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                2,
                It.Is<StatusChangedPayload>(p => p.Status == "away"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
        _broadcaster.Verify(
            b => b.BroadcastStatusChangedAsync(
                1,
                It.Is<StatusChangedPayload>(p => p.Status == "away"),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetPreferredStatusAndMessage_HydrateFromPostgres()
    {
        _users.Setup(u => u.GetByIdAsync(9)).ReturnsAsync(
            new User { Id = 9, PreferredStatus = "dnd", StatusMessage = "focusing" }
        );

        (await Grain(9).GetPreferredStatus()).Should().Be("dnd");
        (await Grain(9).GetStatusMessage()).Should().Be("focusing");
    }

    // -------------------------------------------------------------------------
    // Adapter — the bulk member-list read fans out across grains
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GrainPresenceService_GetStatuses_FansOutAcrossGrains()
    {
        _friends.Setup(f => f.GetFriendIdsAsync(1)).ReturnsAsync(new List<long>());
        await Grain(1).SetOnline("c1"); // user 1 online, user 2 never connects

        var svc = new GrainPresenceService(_cluster.Client, NullLogger<GrainPresenceService>.Instance);
        var statuses = await svc.GetStatusesAsync(new[] { 1L, 2L });

        statuses[1].Should().Be("online");
        statuses[2].Should().Be("offline");
    }
}
