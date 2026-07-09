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
/// The two send-path gates added by the feature audit: slowmode (per-user cooldown via
/// <see cref="ISlowmodeGate"/>, moderators exempt, consumed only after every validation passes)
/// and the SendReply permission bit (replies additionally require it).
/// </summary>
public class MessageServiceSendGateTests
{
    private const long ActorId = 1;
    private const long GuildId = 2;
    private const long ChannelId = 3;
    private const int SlowmodeSeconds = 5;

    private sealed class Harness
    {
        public Mock<IChannelRepository> Channels { get; } = new();
        public Mock<IGuildRepository> Guilds { get; } = new();
        public Mock<IMessagePublisher> Publisher { get; } = new();
        public Mock<IPermissionService> Permissions { get; } = new();
        public Mock<ISlowmodeGate> Slowmode { get; } = new();

        public MessageSentEvent? PublishedEvent { get; private set; }

        public Harness()
        {
            // Safe empty defaults for the mention-resolution path.
            var users = new Mock<IUserRepository>();
            users.Setup(u => u.GetByIdsAsync(It.IsAny<IEnumerable<long>>()))
                .ReturnsAsync(new Dictionary<long, User>());
            Users = users;
            Guilds.Setup(g => g.GetMemberIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long>());
            Guilds.Setup(g => g.GetMembersAsync(It.IsAny<long>())).ReturnsAsync(new List<GuildMember>());
            Roles.Setup(r => r.GetByGuildAsync(It.IsAny<long>())).ReturnsAsync(new List<Role>());
            Publisher
                .Setup(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()))
                .Callback<MessageSentEvent, CancellationToken>((evt, _) => PublishedEvent = evt)
                .Returns(Task.CompletedTask);
            Slowmode
                .Setup(s => s.TryConsumeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        public Mock<IUserRepository> Users { get; }
        public Mock<IRoleRepository> Roles { get; } = new();

        /// <summary>Text channel with the given slowmode; the actor holds the given resolved bits.</summary>
        public void SetUpChannel(int slowmodeSeconds, Permission bits)
        {
            Channels.Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId))
                .ReturnsAsync(new Channel
                {
                    Id = ChannelId, GuildId = GuildId, Type = "text",
                    SlowmodeSeconds = slowmodeSeconds,
                });
            Permissions.Setup(p => p.ResolveAsync(ActorId, GuildId, ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((long)bits);
            Guilds.Setup(g => g.GetMemberAsync(GuildId, ActorId))
                .ReturnsAsync(new GuildMember { UserId = ActorId, GuildId = GuildId });
        }

        public MessageService BuildSut()
        {
            var snowflake = new Mock<ISnowflakeIdGenerator>();
            snowflake.Setup(s => s.NextId()).Returns(999);
            return new MessageService(
                Channels.Object, Guilds.Object, Publisher.Object, snowflake.Object,
                Mock.Of<IMessageRepository>(), Users.Object, Permissions.Object,
                Mock.Of<IFileAttachmentRepository>(), Mock.Of<IDirectMessageRepository>(),
                Mock.Of<IUserBlockRepository>(), Mock.Of<IFriendRepository>(),
                Mock.Of<IPresenceService>(), Mock.Of<IAuditLogService>(),
                Mock.Of<IHubBroadcaster>(), Roles.Object, Slowmode.Object
            );
        }
    }

    private static SendMessageRequest Msg(string content = "hi", long? replyToId = null) =>
        new(content, replyToId);

    // ---- slowmode ----

    [Fact]
    public async Task Send_SlowmodeActiveCooldown_Throws403AndPublishesNothing()
    {
        var h = new Harness();
        h.SetUpChannel(SlowmodeSeconds, Permission.ViewChannel | Permission.SendMessage);
        h.Slowmode
            .Setup(s => s.TryConsumeAsync(ChannelId, ActorId, SlowmodeSeconds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = () => h.BuildSut().SendMessageAsync(ActorId, GuildId, ChannelId, Msg());

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Slowmode*");
        h.PublishedEvent.Should().BeNull();
    }

    [Fact]
    public async Task Send_SlowmodeSlotFree_ConsumesAndPublishes()
    {
        var h = new Harness();
        h.SetUpChannel(SlowmodeSeconds, Permission.ViewChannel | Permission.SendMessage);

        await h.BuildSut().SendMessageAsync(ActorId, GuildId, ChannelId, Msg());

        h.Slowmode.Verify(
            s => s.TryConsumeAsync(ChannelId, ActorId, SlowmodeSeconds, It.IsAny<CancellationToken>()),
            Times.Once);
        h.PublishedEvent.Should().NotBeNull();
    }

    [Fact]
    public async Task Send_ModeratorInSlowmodeChannel_IsExemptAndNeverConsultsTheGate()
    {
        var h = new Harness();
        h.SetUpChannel(
            SlowmodeSeconds,
            Permission.ViewChannel | Permission.SendMessage | Permission.ManageMessages);

        await h.BuildSut().SendMessageAsync(ActorId, GuildId, ChannelId, Msg());

        h.Slowmode.Verify(
            s => s.TryConsumeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        h.PublishedEvent.Should().NotBeNull();
    }

    [Fact]
    public async Task Send_InvalidContent_NeverBurnsTheSlowmodeSlot()
    {
        var h = new Harness();
        h.SetUpChannel(SlowmodeSeconds, Permission.ViewChannel | Permission.SendMessage);

        var act = () => h.BuildSut().SendMessageAsync(
            ActorId, GuildId, ChannelId, Msg(new string('x', 2001)));

        await act.Should().ThrowAsync<ArgumentException>();
        h.Slowmode.Verify(
            s => s.TryConsumeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- SendReply bit ----

    [Fact]
    public async Task Reply_WithoutSendReplyBit_Throws403()
    {
        var h = new Harness();
        h.SetUpChannel(0, Permission.ViewChannel | Permission.SendMessage); // no SendReply

        var act = () => h.BuildSut().SendMessageAsync(
            ActorId, GuildId, ChannelId, Msg(replyToId: 42));

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*reply*");
        h.PublishedEvent.Should().BeNull();
    }

    [Fact]
    public async Task Reply_WithSendReplyBit_Publishes()
    {
        var h = new Harness();
        h.SetUpChannel(0, Permission.ViewChannel | Permission.SendMessage | Permission.SendReply);

        await h.BuildSut().SendMessageAsync(ActorId, GuildId, ChannelId, Msg(replyToId: 42));

        h.PublishedEvent.Should().NotBeNull();
        h.PublishedEvent!.ReplyToId.Should().Be(42);
    }
}
