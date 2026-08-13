using FluentAssertions;
using Harmony.API.Grains;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Harmony.IntegrationTests.Grains;

/// <summary>
/// D2a grain tests. Each test gets its own single-silo <see cref="InProcessTestCluster"/> with the
/// grain's collaborators registered as Moq singletons (the grain resolves <c>IUserRepository</c>
/// through its <c>IServiceScopeFactory</c>, so a singleton registration is enough for a scope to
/// hand it back). Assert the broadcast-first contract: the grain broadcasts the authoritative
/// message, THEN republishes the event flagged <c>BroadcastAlready=true</c> for the persist consumer.
/// Independent of the WebApplicationFactory "Test" hosts (where Orleans stays gated out).
/// </summary>
public class ChannelGrainTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;
    private readonly Mock<IHubBroadcaster> _broadcaster = new();
    private readonly Mock<IMessagePublisher> _publisher = new();
    private readonly Mock<IUserDisplayCache> _displayCache = new();
    private readonly Mock<IUserRepository> _users = new();

    // Records the order the two side effects fire, so the broadcast-then-publish contract is asserted.
    private readonly List<string> _calls = [];

    public async Task InitializeAsync()
    {
        _broadcaster
            .Setup(b => b.BroadcastMessageReceivedAsync(It.IsAny<MessageResponse>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("broadcast"))
            .Returns(Task.CompletedTask);
        _publisher
            .Setup(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("publish"))
            .Returns(Task.CompletedTask);
        _broadcaster
            .Setup(b => b.BroadcastMessageEditedAsync(It.IsAny<MessageEditedPayload>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("broadcast"))
            .Returns(Task.CompletedTask);
        _broadcaster
            .Setup(b => b.BroadcastMessageDeletedAsync(It.IsAny<MessageDeletedPayload>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("broadcast"))
            .Returns(Task.CompletedTask);
        _publisher
            .Setup(p => p.PublishMessageEditedAsync(It.IsAny<MessageEditedEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("publish"))
            .Returns(Task.CompletedTask);
        _publisher
            .Setup(p => p.PublishMessageDeletedAsync(It.IsAny<MessageDeletedEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("publish"))
            .Returns(Task.CompletedTask);

        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        builder.ConfigureSilo((_, siloBuilder) => siloBuilder.AddMemoryGrainStorage("Default"));
        builder.ConfigureHost(hostBuilder =>
        {
            hostBuilder.Services.AddSingleton(_broadcaster.Object);
            hostBuilder.Services.AddSingleton(_publisher.Object);
            hostBuilder.Services.AddSingleton(_displayCache.Object);
            hostBuilder.Services.AddSingleton(_users.Object);
            // Mirror Program.cs: the grain call carries a Domain MessageSentEvent. Registered on the
            // shared host (not the silo builder) so the in-process cluster's CLIENT — which also
            // validates grain-interface serializers — sees it too.
            hostBuilder.Services.AddSerializer(s => s.AddJsonSerializer(
                type => type.Namespace?.StartsWith("Harmony.Domain.Interfaces") == true));
        });

        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    private IChannelGrain Grain(long channelId) => _cluster.Client.GetGrain<IChannelGrain>(channelId);

    private static MessageSentEvent Event(long channelId = 100, long userId = 1, long messageId = 999) =>
        new(
            MessageId: messageId,
            ChannelId: channelId,
            GuildId: 10,
            UserId: userId,
            Content: "hello",
            MessageType: "text",
            AttachmentIds: [],
            MentionIds: [],
            ReplyToId: null,
            SentAt: DateTimeOffset.UtcNow,
            Nonce: "nonce-1"
        );

    [Fact]
    public async Task SendMessage_BroadcastsFirst_ThenRepublishesWithBroadcastAlreadyFlag()
    {
        _displayCache.Setup(c => c.GetAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplay("alice", "avatar-key"));

        await Grain(100).SendMessage(Event(channelId: 100, userId: 1, messageId: 999));

        // Broadcast carries the resolved display + echoes the nonce; happens exactly once.
        _broadcaster.Verify(
            b => b.BroadcastMessageReceivedAsync(
                It.Is<MessageResponse>(m =>
                    m.MessageId == 999 && m.ChannelId == 100 && m.UserId == 1 &&
                    m.Username == "alice" && m.AvatarKey == "avatar-key" && m.Nonce == "nonce-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Republished for persistence with the flag set so the consumer skips its own broadcast.
        _publisher.Verify(
            p => p.PublishMessageSentAsync(
                It.Is<MessageSentEvent>(e => e.MessageId == 999 && e.BroadcastAlready),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Broadcast-first: fan-out precedes the persist-log publish.
        _calls.Should().Equal("broadcast", "publish");
    }

    [Fact]
    public async Task SendMessage_CacheMiss_FallsBackToRepo_AndRepopulatesCache()
    {
        _displayCache.Setup(c => c.GetAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserDisplay?)null);
        _users.Setup(u => u.GetByIdAsync(1))
            .ReturnsAsync(new User { Id = 1, UserName = "bob", AvatarKey = "bob-key" });

        await Grain(100).SendMessage(Event(userId: 1));

        _broadcaster.Verify(
            b => b.BroadcastMessageReceivedAsync(
                It.Is<MessageResponse>(m => m.Username == "bob" && m.AvatarKey == "bob-key"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _displayCache.Verify(
            c => c.SetAsync(1, It.Is<UserDisplay>(d => d.Username == "bob"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendMessage_CacheHit_DoesNotTouchRepo()
    {
        _displayCache.Setup(c => c.GetAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplay("alice", null));

        await Grain(100).SendMessage(Event(userId: 1));

        _users.Verify(u => u.GetByIdAsync(It.IsAny<long>()), Times.Never);
    }

    // ---- edit / delete (D2b) — Scylla is already written synchronously, so broadcast-first, then
    //      republish flagged; the grain needs no display resolve for these. ----

    [Fact]
    public async Task EditMessage_BroadcastsFirst_ThenRepublishesWithBroadcastAlreadyFlag()
    {
        var evt = new MessageEditedEvent(
            MessageId: 999, ChannelId: 100, GuildId: 10, EditedByUserId: 1,
            NewContent: "edited", MentionIds: [], OldMentionIds: [], EditedAt: DateTimeOffset.UtcNow);

        await Grain(100).EditMessage(evt);

        _broadcaster.Verify(
            b => b.BroadcastMessageEditedAsync(
                It.Is<MessageEditedPayload>(p => p.MessageId == 999 && p.NewContent == "edited"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _publisher.Verify(
            p => p.PublishMessageEditedAsync(
                It.Is<MessageEditedEvent>(e => e.MessageId == 999 && e.BroadcastAlready),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _users.Verify(u => u.GetByIdAsync(It.IsAny<long>()), Times.Never);
        _calls.Should().Equal("broadcast", "publish");
    }

    [Fact]
    public async Task DeleteMessage_BroadcastsFirst_ThenRepublishesWithBroadcastAlreadyFlag()
    {
        var evt = new MessageDeletedEvent(
            MessageId: 999, ChannelId: 100, GuildId: 10, DeletedByUserId: 1,
            DeletedAt: DateTimeOffset.UtcNow);

        await Grain(100).DeleteMessage(evt);

        _broadcaster.Verify(
            b => b.BroadcastMessageDeletedAsync(
                It.Is<MessageDeletedPayload>(p => p.MessageId == 999 && p.ChannelId == 100),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _publisher.Verify(
            p => p.PublishMessageDeletedAsync(
                It.Is<MessageDeletedEvent>(e => e.MessageId == 999 && e.BroadcastAlready),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _calls.Should().Equal("broadcast", "publish");
    }

    [Fact]
    public async Task SendMessage_DisplayResolveThrows_BroadcastsUnknown_AndStillPublishes()
    {
        // Fail-open: a Redis hiccup degrades to the "Unknown" placeholder, never blocks the send.
        _displayCache.Setup(c => c.GetAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Grain(100).SendMessage(Event(userId: 1));

        _broadcaster.Verify(
            b => b.BroadcastMessageReceivedAsync(
                It.Is<MessageResponse>(m => m.Username == "Unknown" && m.AvatarKey == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _publisher.Verify(
            p => p.PublishMessageSentAsync(
                It.Is<MessageSentEvent>(e => e.BroadcastAlready), It.IsAny<CancellationToken>()),
            Times.Once);
        _calls.Should().Equal("broadcast", "publish");
    }
}
