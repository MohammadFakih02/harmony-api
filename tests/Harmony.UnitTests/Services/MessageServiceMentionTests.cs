using FluentAssertions;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Moq;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Server-side mention resolution (the send/edit path's <c>ResolveMentionsAsync</c> helper).
/// Covers candidate scoping (guild members vs. DM peer), @everyone/@here permission-gating,
/// and the @here online-filter — all via the public SendMessageAsync surface, asserting on
/// the MentionIds captured in the published MessageSentEvent.
/// </summary>
public class MessageServiceMentionTests
{
    private const long ActorId = 1;
    private const long GuildId = 2;
    private const long ChannelId = 3;
    private const long BobId = 10;
    private const long CarolId = 11;

    private sealed class Harness
    {
        public Mock<IChannelRepository> Channels { get; } = new();
        public Mock<IGuildRepository> Guilds { get; } = new();
        public Mock<IMessagePublisher> Publisher { get; } = new();
        public Mock<ISnowflakeIdGenerator> Snowflake { get; } = new();
        public Mock<IMessageRepository> Messages { get; } = new();
        public Mock<IUserRepository> Users { get; } = new();
        public Mock<IPermissionService> Permissions { get; } = new();
        public Mock<IFileAttachmentRepository> Files { get; } = new();
        public Mock<IDirectMessageRepository> Dms { get; } = new();
        public Mock<IUserBlockRepository> Blocks { get; } = new();
        public Mock<IPresenceService> Presence { get; } = new();

        public MessageSentEvent? PublishedEvent { get; private set; }
        public MessageEditedEvent? PublishedEditEvent { get; private set; }

        public MessageService BuildSut()
        {
            Snowflake.Setup(s => s.NextId()).Returns(999);
            Publisher
                .Setup(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()))
                .Callback<MessageSentEvent, CancellationToken>((evt, _) => PublishedEvent = evt)
                .Returns(Task.CompletedTask);
            Publisher
                .Setup(p => p.PublishMessageEditedAsync(It.IsAny<MessageEditedEvent>(), It.IsAny<CancellationToken>()))
                .Callback<MessageEditedEvent, CancellationToken>((evt, _) => PublishedEditEvent = evt)
                .Returns(Task.CompletedTask);

            return new MessageService(
                Channels.Object, Guilds.Object, Publisher.Object, Snowflake.Object,
                Messages.Object, Users.Object, Permissions.Object, Files.Object,
                Dms.Object, Blocks.Object, Presence.Object
            );
        }

        public void SetUpGuildSendContext()
        {
            Channels.Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId))
                .ReturnsAsync(new Channel { Id = ChannelId, GuildId = GuildId, Type = "text" });
            Permissions.Setup(p => p.ResolveAsync(ActorId, GuildId, ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((long)(Permission.ViewChannel | Permission.SendMessage));
            Guilds.Setup(g => g.GetMemberAsync(GuildId, ActorId))
                .ReturnsAsync(new GuildMember { UserId = ActorId, GuildId = GuildId });
        }

        public void SetUpGuildMembers(IReadOnlyDictionary<long, string> members)
        {
            Guilds.Setup(g => g.GetMemberIdsAsync(GuildId)).ReturnsAsync(members.Keys.ToList());
            Users.Setup(u => u.GetByIdsAsync(It.IsAny<IEnumerable<long>>()))
                .ReturnsAsync(members.ToDictionary(kv => kv.Key, kv => new User { Id = kv.Key, UserName = kv.Value }));
        }

        public void SetUpDmSendContext(List<long> participantIds)
        {
            Channels.Setup(c => c.GetByIdAsync(ChannelId))
                .ReturnsAsync(new Channel { Id = ChannelId, GuildId = null, Type = "dm" });
            Dms.Setup(d => d.GetParticipantIdsAsync(ChannelId)).ReturnsAsync(participantIds);
            Blocks.Setup(b => b.AreBlockedAsync(It.IsAny<long>(), It.IsAny<long>())).ReturnsAsync(false);
        }
    }

    [Fact]
    public async Task SendMessageAsync_ShouldResolveMentionedGuildMember()
    {
        var h = new Harness();
        h.SetUpGuildSendContext();
        h.SetUpGuildMembers(new Dictionary<long, string> { [ActorId] = "actor", [BobId] = "bob" });
        var sut = h.BuildSut();

        await sut.SendMessageAsync(ActorId, GuildId, ChannelId, new SendMessageRequest(Content: "hey @bob"));

        h.PublishedEvent!.MentionIds.Should().BeEquivalentTo(new List<long> { BobId });
    }

    [Fact]
    public async Task EditMessageAsync_ShouldPublishPreEditMentionSet_SoTheConsumerCanDiff()
    {
        // Regression: the consumer can't re-read the "old" mention set (the synchronous
        // edit overwrites it first), so MessageService must capture it and ship it in the
        // event. A message previously mentioning bob, edited to mention carol instead, must
        // publish OldMentionIds=[bob] and MentionIds=[carol].
        var h = new Harness();
        h.SetUpGuildSendContext();
        h.SetUpGuildMembers(new Dictionary<long, string>
        {
            [ActorId] = "actor",
            [BobId] = "bob",
            [CarolId] = "carol",
        });
        h.Messages
            .Setup(m => m.GetByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message
            {
                MessageId = 999,
                ChannelId = ChannelId,
                UserId = ActorId,
                MentionIds = new List<long> { BobId },
            });
        var sut = h.BuildSut();

        await sut.EditMessageAsync(ActorId, GuildId, ChannelId, 999, new EditMessageRequest("now @carol"));

        h.PublishedEditEvent.Should().NotBeNull();
        h.PublishedEditEvent!.OldMentionIds.Should().BeEquivalentTo(new List<long> { BobId });
        h.PublishedEditEvent!.MentionIds.Should().BeEquivalentTo(new List<long> { CarolId });
    }

    [Fact]
    public async Task SendMessageAsync_ShouldNotResolve_NonMemberUsername()
    {
        var h = new Harness();
        h.SetUpGuildSendContext();
        // "bob" is a real registered username, but NOT a member of this guild — never resolves.
        h.SetUpGuildMembers(new Dictionary<long, string> { [ActorId] = "actor" });
        var sut = h.BuildSut();

        await sut.SendMessageAsync(ActorId, GuildId, ChannelId, new SendMessageRequest(Content: "hey @bob"));

        h.PublishedEvent!.MentionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessageAsync_ShouldExpandEveryone_WhenActorHasPermission()
    {
        var h = new Harness();
        h.SetUpGuildSendContext();
        h.SetUpGuildMembers(new Dictionary<long, string>
        {
            [ActorId] = "actor", [BobId] = "bob", [CarolId] = "carol",
        });
        h.Permissions
            .Setup(p => p.HasAsync(ActorId, GuildId, Permission.MentionEveryone, ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = h.BuildSut();

        await sut.SendMessageAsync(ActorId, GuildId, ChannelId, new SendMessageRequest(Content: "@everyone gm"));

        h.PublishedEvent!.MentionIds.Should().BeEquivalentTo(new List<long> { ActorId, BobId, CarolId });
    }

    [Fact]
    public async Task SendMessageAsync_ShouldNotExpandEveryone_WithoutPermission_AndStillSend()
    {
        var h = new Harness();
        h.SetUpGuildSendContext();
        h.SetUpGuildMembers(new Dictionary<long, string>
        {
            [ActorId] = "actor", [BobId] = "bob", [CarolId] = "carol",
        });
        h.Permissions
            .Setup(p => p.HasAsync(ActorId, GuildId, Permission.MentionEveryone, ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = h.BuildSut();

        var response = await sut.SendMessageAsync(ActorId, GuildId, ChannelId, new SendMessageRequest(Content: "@everyone gm"));

        // Mentions are a notification side effect, never a send-blocker — the message still sends.
        response.Content.Should().Be("@everyone gm");
        h.PublishedEvent!.MentionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessageAsync_ShouldExpandHere_OnlyToOnlineMembers()
    {
        var h = new Harness();
        h.SetUpGuildSendContext();
        h.SetUpGuildMembers(new Dictionary<long, string>
        {
            [ActorId] = "actor", [BobId] = "bob", [CarolId] = "carol",
        });
        h.Permissions
            .Setup(p => p.HasAsync(ActorId, GuildId, Permission.MentionEveryone, ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        h.Presence
            .Setup(p => p.GetStatusesAsync(It.IsAny<IEnumerable<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, string>
            {
                [ActorId] = "online",
                [BobId] = "offline",
                [CarolId] = "away",
            });
        var sut = h.BuildSut();

        await sut.SendMessageAsync(ActorId, GuildId, ChannelId, new SendMessageRequest(Content: "@here standup"));

        h.PublishedEvent!.MentionIds.Should().BeEquivalentTo(new List<long> { ActorId, CarolId });
    }

    [Fact]
    public async Task SendMessageAsync_InDm_ShouldOnlyResolvePeer_AndIgnoreEveryoneHere()
    {
        var h = new Harness();
        h.SetUpDmSendContext([ActorId, BobId]);
        h.Users.Setup(u => u.GetByIdsAsync(It.IsAny<IEnumerable<long>>()))
            .ReturnsAsync(new Dictionary<long, User>
            {
                [ActorId] = new() { Id = ActorId, UserName = "actor" },
                [BobId] = new() { Id = BobId, UserName = "bob" },
            });
        var sut = h.BuildSut();

        await sut.SendMessageAsync(
            ActorId, guildId: null, ChannelId,
            new SendMessageRequest(Content: "@bob @everyone @here")
        );

        // @everyone/@here are guild-only literals; in a DM they're just plain (non-matching) text.
        h.PublishedEvent!.MentionIds.Should().BeEquivalentTo(new List<long> { BobId });
        h.Permissions.Verify(
            p => p.HasAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<Permission>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }
}
