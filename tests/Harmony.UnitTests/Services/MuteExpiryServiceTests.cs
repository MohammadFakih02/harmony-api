using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for the MuteExpiryService sweep: it deletes expired mutes (via the
/// repository) and broadcasts MuteExpired once per swept mute to its owner. A bad
/// push must not abort the fan-out.
/// </summary>
public class MuteExpiryServiceTests
{
    private static (
        MuteExpiryService sut,
        Mock<IUserMuteRepository> repo,
        Mock<IHubBroadcaster> broadcaster
    ) BuildSut()
    {
        var repo = new Mock<IUserMuteRepository>();
        var broadcaster = new Mock<IHubBroadcaster>();

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(IUserMuteRepository))).Returns(repo.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var sut = new MuteExpiryService(
            scopeFactory.Object,
            broadcaster.Object,
            NullLogger<MuteExpiryService>.Instance
        );

        return (sut, repo, broadcaster);
    }

    private static UserMute Mute(long userId, long targetId, string type) =>
        new()
        {
            UserId = userId,
            TargetId = targetId,
            TargetType = type,
            MutedUntil = 1,
            CreatedAt = 1,
        };

    [Fact]
    public async Task RunOnce_BroadcastsMuteExpired_PerSweptMute()
    {
        var (sut, repo, broadcaster) = BuildSut();
        var expired = new List<UserMute>
        {
            Mute(1, 100, MuteTargetType.Guild),
            Mute(2, 200, MuteTargetType.User),
        };
        repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<long>())).ReturnsAsync(expired);

        var count = await sut.RunOnceAsync();

        count.Should().Be(2);
        broadcaster.Verify(
            b =>
                b.BroadcastMuteExpiredAsync(
                    1,
                    It.Is<MuteExpiredPayload>(p => p.TargetId == 100 && p.TargetType == "guild"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        broadcaster.Verify(
            b =>
                b.BroadcastMuteExpiredAsync(
                    2,
                    It.Is<MuteExpiredPayload>(p => p.TargetId == 200 && p.TargetType == "user"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task RunOnce_WithNothingExpired_DoesNotBroadcast()
    {
        var (sut, repo, broadcaster) = BuildSut();
        repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<long>())).ReturnsAsync(new List<UserMute>());

        var count = await sut.RunOnceAsync();

        count.Should().Be(0);
        broadcaster.Verify(
            b =>
                b.BroadcastMuteExpiredAsync(
                    It.IsAny<long>(),
                    It.IsAny<MuteExpiredPayload>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task RunOnce_OneFailedBroadcast_DoesNotAbortTheRest()
    {
        var (sut, repo, broadcaster) = BuildSut();
        var expired = new List<UserMute>
        {
            Mute(1, 100, MuteTargetType.Guild),
            Mute(2, 200, MuteTargetType.User),
        };
        repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<long>())).ReturnsAsync(expired);

        // First push throws; the loop must still attempt the second.
        broadcaster
            .Setup(b =>
                b.BroadcastMuteExpiredAsync(1, It.IsAny<MuteExpiredPayload>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new InvalidOperationException("boom"));

        var count = await sut.RunOnceAsync();

        count.Should().Be(2);
        broadcaster.Verify(
            b =>
                b.BroadcastMuteExpiredAsync(2, It.IsAny<MuteExpiredPayload>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
