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
/// Unit tests for the push-outbox dispatcher: the per-recipient gate matrix in
/// ProcessAsync (offline-only, DnD, PushEnabled, dm mute/block, dm fan-out excludes),
/// subscription pruning on Gone, and RunOnceAsync's retry/backoff/dead-row bookkeeping.
/// All seams mocked; the harness defaults to the "offline user with one subscription,
/// everything enabled" happy path so each test flips exactly one gate.
/// </summary>
public class PushNotificationServiceTests
{
    private sealed class Harness
    {
        public readonly Mock<IPresenceService> Presence = new();
        public readonly Mock<IPushSubscriptionRepository> Subscriptions = new();
        public readonly Mock<INotificationPreferenceRepository> Preferences = new();
        public readonly Mock<IDirectMessageRepository> Dms = new();
        public readonly Mock<IUserMuteRepository> Mutes = new();
        public readonly Mock<IUserBlockRepository> Blocks = new();
        public readonly Mock<IUserRepository> Users = new();
        public readonly Mock<IChannelRepository> Channels = new();
        public readonly Mock<IMessageRepository> Messages = new();
        public readonly Mock<IPushOutboxRepository> Outbox = new();
        public readonly Mock<IWebPushSender> Sender = new();
        public readonly List<string> SentPayloads = [];
        public readonly IServiceProvider Provider;
        public readonly PushNotificationService Sut;

        public Harness()
        {
            // Happy-path defaults — offline, online-preferred, no pref row (= enabled),
            // one subscription, sends succeed.
            Presence
                .Setup(p => p.IsConnectedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            Presence
                .Setup(p =>
                    p.GetPreferredStatusAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())
                )
                .ReturnsAsync("online");
            Preferences
                .Setup(p => p.GetAsync(It.IsAny<long>()))
                .ReturnsAsync((NotificationPreference?)null);
            Subscriptions
                .Setup(s => s.GetForUserAsync(It.IsAny<long>()))
                .ReturnsAsync((long userId) => new List<UserPushSubscription> { Sub(userId) });
            Mutes
                .Setup(m =>
                    m.IsMutedAsync(
                        It.IsAny<long>(),
                        It.IsAny<long>(),
                        It.IsAny<string>(),
                        It.IsAny<long>()
                    )
                )
                .ReturnsAsync(false);
            Blocks
                .Setup(b => b.AreBlockedAsync(It.IsAny<long>(), It.IsAny<long>()))
                .ReturnsAsync(false);
            Users
                .Setup(u => u.GetByIdAsync(It.IsAny<long>()))
                .ReturnsAsync(new User { UserName = "alice" });
            Messages
                .Setup(m => m.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Message?)null);
            Sender
                .Setup(s =>
                    s.SendAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback(
                    (string _, string _, string _, string payload, CancellationToken _) =>
                        SentPayloads.Add(payload)
                )
                .ReturnsAsync(PushSendResult.Sent);

            var sp = new Mock<IServiceProvider>();
            sp.Setup(s => s.GetService(typeof(IPresenceService))).Returns(Presence.Object);
            sp.Setup(s => s.GetService(typeof(IPushSubscriptionRepository)))
                .Returns(Subscriptions.Object);
            sp.Setup(s => s.GetService(typeof(INotificationPreferenceRepository)))
                .Returns(Preferences.Object);
            sp.Setup(s => s.GetService(typeof(IDirectMessageRepository))).Returns(Dms.Object);
            sp.Setup(s => s.GetService(typeof(IUserMuteRepository))).Returns(Mutes.Object);
            sp.Setup(s => s.GetService(typeof(IUserBlockRepository))).Returns(Blocks.Object);
            sp.Setup(s => s.GetService(typeof(IUserRepository))).Returns(Users.Object);
            sp.Setup(s => s.GetService(typeof(IChannelRepository))).Returns(Channels.Object);
            sp.Setup(s => s.GetService(typeof(IMessageRepository))).Returns(Messages.Object);
            sp.Setup(s => s.GetService(typeof(IPushOutboxRepository))).Returns(Outbox.Object);
            Provider = sp.Object;

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(Provider);
            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            Sut = new PushNotificationService(
                scopeFactory.Object,
                Mock.Of<IPushDispatchNudge>(),
                Sender.Object,
                NullLogger<PushNotificationService>.Instance
            );
        }

        public void VerifySendCount(Times times) =>
            Sender.Verify(
                s =>
                    s.SendAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()
                    ),
                times
            );
    }

    private static UserPushSubscription Sub(long userId, string endpoint = "https://push/e1") =>
        new()
        {
            Id = userId * 10,
            UserId = userId,
            Endpoint = endpoint,
            P256dh = "p",
            AuthKey = "a",
        };

    private static PushOutboxMessage MentionRow(long recipientId = 5) =>
        new()
        {
            Id = 1,
            Kind = PushKind.Mention,
            RecipientId = recipientId,
            ActorId = 9,
            GuildId = 100,
            ChannelId = 200,
            MessageId = 300,
        };

    private static PushOutboxMessage DmRow() =>
        new()
        {
            Id = 2,
            Kind = PushKind.Dm,
            RecipientId = 0,
            ActorId = 9,
            ChannelId = 200,
            MessageId = 300,
        };

    // ---- ProcessAsync gate matrix ----

    [Fact]
    public async Task Process_OfflineRecipient_SendsToEverySubscription()
    {
        var h = new Harness();
        h.Subscriptions.Setup(s => s.GetForUserAsync(5))
            .ReturnsAsync(
                new List<UserPushSubscription> { Sub(5, "https://push/e1"), Sub(5, "https://push/e2") }
            );

        await h.Sut.ProcessAsync(h.Provider, MentionRow());

        h.VerifySendCount(Times.Exactly(2));
    }

    [Fact]
    public async Task Process_ConnectedRecipient_SkipsThePush()
    {
        var h = new Harness();
        h.Presence.Setup(p => p.IsConnectedAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await h.Sut.ProcessAsync(h.Provider, MentionRow());

        h.VerifySendCount(Times.Never());
    }

    [Fact]
    public async Task Process_DndPreferredStatus_SkipsThePush()
    {
        var h = new Harness();
        h.Presence.Setup(p => p.GetPreferredStatusAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync("dnd");

        await h.Sut.ProcessAsync(h.Provider, MentionRow());

        h.VerifySendCount(Times.Never());
    }

    [Fact]
    public async Task Process_PushDisabledPreference_SkipsThePush()
    {
        var h = new Harness();
        h.Preferences.Setup(p => p.GetAsync(5))
            .ReturnsAsync(new NotificationPreference { UserId = 5, PushEnabled = false });

        await h.Sut.ProcessAsync(h.Provider, MentionRow());

        h.VerifySendCount(Times.Never());
    }

    [Fact]
    public async Task Process_NoSubscriptions_IsANoOp()
    {
        var h = new Harness();
        h.Subscriptions.Setup(s => s.GetForUserAsync(5))
            .ReturnsAsync(new List<UserPushSubscription>());

        await h.Sut.ProcessAsync(h.Provider, MentionRow());

        h.VerifySendCount(Times.Never());
    }

    [Fact]
    public async Task Process_GoneSubscription_IsPruned_AndOthersStillSend()
    {
        var h = new Harness();
        var dead = Sub(5, "https://push/dead");
        var alive = Sub(5, "https://push/alive");
        h.Subscriptions.Setup(s => s.GetForUserAsync(5))
            .ReturnsAsync(new List<UserPushSubscription> { dead, alive });
        h.Sender.Setup(s =>
                s.SendAsync(
                    "https://push/dead",
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(PushSendResult.Gone);

        await h.Sut.ProcessAsync(h.Provider, MentionRow());

        h.Subscriptions.Verify(s => s.Remove(dead), Times.Once);
        h.Subscriptions.Verify(s => s.Remove(alive), Times.Never);
        h.VerifySendCount(Times.Exactly(2));
    }

    [Fact]
    public async Task Process_MentionPayload_CarriesActorChannelAndUrl()
    {
        var h = new Harness();
        h.Channels.Setup(c => c.GetByIdAsync(200)).ReturnsAsync(new Channel { Name = "general" });
        h.Messages.Setup(m => m.GetByIdAsync(300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message { MessageId = 300, Content = "hey @you" });

        await h.Sut.ProcessAsync(h.Provider, MentionRow());

        h.SentPayloads.Should().ContainSingle();
        var payload = h.SentPayloads[0];
        payload.Should().Contain("alice mentioned you in #general");
        payload.Should().Contain("hey @you");
        payload.Should().Contain("/app/guilds/100/channels/200");
        payload.Should().Contain("channel-200");
    }

    // ---- dm fan-out ----

    [Fact]
    public async Task Process_DmRow_FansOutToParticipants_MinusActorAndExcludes()
    {
        var h = new Harness();
        var row = DmRow();
        row.ExcludeUserIds = "7"; // already got a mention/reply push for this message
        h.Dms.Setup(d => d.GetParticipantIdsAsync(200))
            .ReturnsAsync(new List<long> { 9, 7, 5, 6 }); // 9 = actor

        await h.Sut.ProcessAsync(h.Provider, row);

        h.Subscriptions.Verify(s => s.GetForUserAsync(5), Times.Once);
        h.Subscriptions.Verify(s => s.GetForUserAsync(6), Times.Once);
        h.Subscriptions.Verify(s => s.GetForUserAsync(7), Times.Never);
        h.Subscriptions.Verify(s => s.GetForUserAsync(9), Times.Never);
        h.VerifySendCount(Times.Exactly(2));
    }

    [Fact]
    public async Task Process_DmRow_MutedActor_SuppressesThatRecipientOnly()
    {
        var h = new Harness();
        h.Dms.Setup(d => d.GetParticipantIdsAsync(200)).ReturnsAsync(new List<long> { 9, 5, 6 });
        h.Mutes.Setup(m => m.IsMutedAsync(5, 9, MuteTargetType.User, It.IsAny<long>()))
            .ReturnsAsync(true);

        await h.Sut.ProcessAsync(h.Provider, DmRow());

        h.Subscriptions.Verify(s => s.GetForUserAsync(5), Times.Never);
        h.Subscriptions.Verify(s => s.GetForUserAsync(6), Times.Once);
        h.VerifySendCount(Times.Once());
    }

    [Fact]
    public async Task Process_DmRow_BlockedPair_SuppressesTheRecipient()
    {
        var h = new Harness();
        h.Dms.Setup(d => d.GetParticipantIdsAsync(200)).ReturnsAsync(new List<long> { 9, 5 });
        h.Blocks.Setup(b => b.AreBlockedAsync(9, 5)).ReturnsAsync(true);

        await h.Sut.ProcessAsync(h.Provider, DmRow());

        h.VerifySendCount(Times.Never());
    }

    [Fact]
    public async Task Process_DmPayload_UsesActorAsTitle_AndDmUrl()
    {
        var h = new Harness();
        h.Dms.Setup(d => d.GetParticipantIdsAsync(200)).ReturnsAsync(new List<long> { 9, 5 });
        h.Messages.Setup(m => m.GetByIdAsync(300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message { MessageId = 300, Content = "hello there" });

        await h.Sut.ProcessAsync(h.Provider, DmRow());

        h.SentPayloads.Should().ContainSingle();
        var payload = h.SentPayloads[0];
        payload.Should().Contain("\"title\":\"alice\"");
        payload.Should().Contain("hello there");
        payload.Should().Contain("/app/dm/200");
    }

    // ---- RunOnceAsync bookkeeping ----

    [Fact]
    public async Task RunOnce_ProcessedRow_IsDeleted_AndSaved()
    {
        var h = new Harness();
        var row = MentionRow();
        h.Outbox.SetupSequence(o => o.GetDueAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<PushOutboxMessage> { row })
            .ReturnsAsync(new List<PushOutboxMessage>());

        var handled = await h.Sut.RunOnceAsync();

        handled.Should().Be(1);
        h.Outbox.Verify(o => o.Remove(row), Times.Once);
        h.Outbox.Verify(o => o.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task RunOnce_TransientFailure_BumpsAttemptsAndBackoff_KeepsTheRow()
    {
        var h = new Harness();
        var row = MentionRow();
        h.Presence.Setup(p => p.IsConnectedAsync(5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis hiccup"));
        h.Outbox.SetupSequence(o => o.GetDueAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<PushOutboxMessage> { row })
            .ReturnsAsync(new List<PushOutboxMessage>());

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await h.Sut.RunOnceAsync();

        row.Attempts.Should().Be(1);
        row.NextAttemptAt.Should().BeGreaterThanOrEqualTo(before + 60_000);
        h.Outbox.Verify(o => o.Remove(row), Times.Never);
        h.Outbox.Verify(o => o.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task RunOnce_ExhaustedAttempts_DropsTheRow()
    {
        var h = new Harness();
        var row = MentionRow();
        row.Attempts = PushNotificationService.MaxAttempts - 1;
        h.Presence.Setup(p => p.IsConnectedAsync(5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("still down"));
        h.Outbox.SetupSequence(o => o.GetDueAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<PushOutboxMessage> { row })
            .ReturnsAsync(new List<PushOutboxMessage>());

        await h.Sut.RunOnceAsync();

        h.Outbox.Verify(o => o.Remove(row), Times.Once);
    }

    [Fact]
    public async Task RunOnce_OneBadRow_DoesNotAbortTheRest()
    {
        var h = new Harness();
        var bad = MentionRow(recipientId: 1);
        var good = MentionRow(recipientId: 2);
        good.Id = 3;
        h.Presence.Setup(p => p.IsConnectedAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        h.Outbox.SetupSequence(o => o.GetDueAsync(It.IsAny<long>(), It.IsAny<int>()))
            .ReturnsAsync(new List<PushOutboxMessage> { bad, good })
            .ReturnsAsync(new List<PushOutboxMessage>());

        var handled = await h.Sut.RunOnceAsync();

        handled.Should().Be(2);
        h.Outbox.Verify(o => o.Remove(good), Times.Once);
        h.Outbox.Verify(o => o.Remove(bad), Times.Never);
        h.VerifySendCount(Times.Once());
    }
}
