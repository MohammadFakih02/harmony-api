using FluentAssertions;
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
/// Unit tests for the StatusExpiryService sweep: it reverts expired preferred statuses
/// to online, clears expired custom status messages, and re-broadcasts the reverted
/// statuses (a bad push must not abort the rest).
/// </summary>
public class StatusExpiryServiceTests
{
    private static (
        StatusExpiryService sut,
        Mock<IUserRepository> users,
        Mock<IPresenceService> presence
    ) BuildSut()
    {
        var users = new Mock<IUserRepository>();
        var presence = new Mock<IPresenceService>();

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(IUserRepository))).Returns(users.Object);
        sp.Setup(s => s.GetService(typeof(IPresenceService))).Returns(presence.Object);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var sut = new StatusExpiryService(
            scopeFactory.Object,
            NullLogger<StatusExpiryService>.Instance
        );

        return (sut, users, presence);
    }

    [Fact]
    public async Task RunOnce_RevertsExpiredStatus_ClearsExpiredMessage_AndBroadcastsReverted()
    {
        var (sut, users, presence) = BuildSut();
        var past = 1L;
        var statusExpired = new User
        {
            Id = 1,
            PreferredStatus = "dnd",
            PreferredStatusExpiresAt = past,
        };
        var messageExpired = new User
        {
            Id = 2,
            StatusMessage = "brb",
            StatusMessageExpiresAt = past,
        };
        users
            .Setup(u => u.GetUsersWithExpiredStatusAsync(It.IsAny<long>()))
            .ReturnsAsync(new List<User> { statusExpired, messageExpired });

        var count = await sut.RunOnceAsync();

        count.Should().Be(2);
        // Status user reverted to online with its expiry cleared.
        statusExpired.PreferredStatus.Should().Be("online");
        statusExpired.PreferredStatusExpiresAt.Should().BeNull();
        // Message user's custom status cleared.
        messageExpired.StatusMessage.Should().BeNull();
        messageExpired.StatusMessageExpiresAt.Should().BeNull();

        users.Verify(u => u.SaveChangesAsync(), Times.Once);
        // Only the reverted status is re-broadcast (the message-only user isn't).
        presence.Verify(
            p => p.SetPreferredStatusAsync(1, "online", It.IsAny<CancellationToken>()),
            Times.Once
        );
        presence.Verify(
            p => p.SetPreferredStatusAsync(2, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task RunOnce_WithNothingExpired_DoesNothing()
    {
        var (sut, users, presence) = BuildSut();
        users
            .Setup(u => u.GetUsersWithExpiredStatusAsync(It.IsAny<long>()))
            .ReturnsAsync(new List<User>());

        var count = await sut.RunOnceAsync();

        count.Should().Be(0);
        users.Verify(u => u.SaveChangesAsync(), Times.Never);
        presence.Verify(
            p => p.SetPreferredStatusAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task RunOnce_OneFailedBroadcast_DoesNotAbortTheRest()
    {
        var (sut, users, presence) = BuildSut();
        var a = new User { Id = 1, PreferredStatus = "dnd", PreferredStatusExpiresAt = 1 };
        var b = new User { Id = 2, PreferredStatus = "away", PreferredStatusExpiresAt = 1 };
        users
            .Setup(u => u.GetUsersWithExpiredStatusAsync(It.IsAny<long>()))
            .ReturnsAsync(new List<User> { a, b });
        presence
            .Setup(p => p.SetPreferredStatusAsync(1, "online", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var count = await sut.RunOnceAsync();

        count.Should().Be(2);
        presence.Verify(
            p => p.SetPreferredStatusAsync(2, "online", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
