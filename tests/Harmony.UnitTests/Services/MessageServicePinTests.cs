using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// message-pinning authorization + rules (flow #16): guild pins require <c>PinMessages</c>; DM/group
/// pins require participation; the 50-cap, cross-channel guard, deleted-message rejection, and
/// idempotent re-pin are enforced in <see cref="MessageService"/>; a successful pin broadcasts
/// <c>MessagePinned</c>.
/// </summary>
public class MessageServicePinTests
{
    private const long GuildId = 100;
    private const long ChannelId = 200;
    private const long MessageId = 300;
    private const long ActorId = 1;
    private const long AuthorId = 2;

    private sealed class Ctx
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
        public Mock<IAuditLogService> AuditLog { get; } = new();
        public Mock<IHubBroadcaster> Broadcaster { get; } = new();

        public Ctx()
        {
            Snowflake.Setup(s => s.NextId()).Returns(999);
        }

        public MessageService BuildSut() =>
            new(
                Channels.Object, Guilds.Object, Publisher.Object, Snowflake.Object,
                Messages.Object, Users.Object, Permissions.Object, Files.Object,
                Dms.Object, Blocks.Object, Presence.Object, AuditLog.Object,
                Broadcaster.Object
            );

        /// <summary>Guild channel exists; the actor holds (or lacks) PinMessages; the message lives here.</summary>
        public void SetUpGuild(bool canPin, bool deleted = false)
        {
            Channels.Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId))
                .ReturnsAsync(new Channel { Id = ChannelId, GuildId = GuildId, Type = "text" });
            Permissions.Setup(p => p.HasAsync(ActorId, GuildId, Permission.PinMessages, ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(canPin);
            Messages.Setup(m => m.GetByIdAsync(MessageId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Message { MessageId = MessageId, ChannelId = ChannelId, UserId = AuthorId, IsDeleted = deleted });
        }

        public void SetUpPins(params long[] pinnedMessageIds) =>
            Messages.Setup(m => m.GetPinnedAsync(ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(pinnedMessageIds.Select(id => new PinnedMessage
                {
                    ChannelId = ChannelId, MessageId = id, PinnedAt = id, PinnedBy = ActorId
                }).ToList());

        /// <summary>DM channel exists; the actor is (or isn't) a participant; the message lives here.</summary>
        public void SetUpDm(bool participant)
        {
            Channels.Setup(c => c.GetByIdAsync(ChannelId))
                .ReturnsAsync(new Channel { Id = ChannelId, GuildId = null, Type = "dm" });
            Dms.Setup(d => d.IsParticipantAsync(ChannelId, ActorId)).ReturnsAsync(participant);
            Messages.Setup(m => m.GetByIdAsync(MessageId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Message { MessageId = MessageId, ChannelId = ChannelId, UserId = ActorId });
        }
    }

    [Fact]
    public async Task GuildPin_WithPermission_PinsBroadcastsAndAudits()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canPin: true);
        ctx.SetUpPins(); // no existing pins

        await ctx.BuildSut().PinMessageAsync(ActorId, GuildId, ChannelId, MessageId);

        ctx.Messages.Verify(m => m.PinAsync(ChannelId, MessageId, ActorId, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Broadcaster.Verify(b => b.BroadcastMessagePinnedAsync(
            It.Is<MessagePinPayload>(p => p.MessageId == MessageId && p.ChannelId == ChannelId),
            It.IsAny<CancellationToken>()), Times.Once);
        // Guild-only system notice is published through the message pipeline.
        ctx.Publisher.Verify(p => p.PublishMessageSentAsync(
            It.Is<MessageSentEvent>(e => e.MessageType == "pin" && e.ChannelId == ChannelId),
            It.IsAny<CancellationToken>()), Times.Once);
        ctx.AuditLog.Verify(a => a.LogAsync(
            GuildId, ActorId, AuditLogAction.MessagePin, MessageId,
            It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GuildPin_WithoutPermission_Throws403_AndDoesNotPin()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canPin: false);

        var act = () => ctx.BuildSut().PinMessageAsync(ActorId, GuildId, ChannelId, MessageId);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ctx.Messages.Verify(m => m.PinAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GuildPin_MessageInAnotherChannel_Throws403()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canPin: true);
        // Message actually belongs to a different channel.
        ctx.Messages.Setup(m => m.GetByIdAsync(MessageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message { MessageId = MessageId, ChannelId = 999, UserId = AuthorId });

        var act = () => ctx.BuildSut().PinMessageAsync(ActorId, GuildId, ChannelId, MessageId);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task GuildPin_DeletedMessage_Throws400()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canPin: true, deleted: true);

        var act = () => ctx.BuildSut().PinMessageAsync(ActorId, GuildId, ChannelId, MessageId);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GuildPin_AtCap_Throws400()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canPin: true);
        ctx.SetUpPins(Enumerable.Range(1, MessageService.MaxPinsPerChannel).Select(i => (long)(1000 + i)).ToArray());

        var act = () => ctx.BuildSut().PinMessageAsync(ActorId, GuildId, ChannelId, MessageId);

        await act.Should().ThrowAsync<ArgumentException>();
        ctx.Messages.Verify(m => m.PinAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GuildPin_AlreadyPinned_IsIdempotentNoOp()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canPin: true);
        ctx.SetUpPins(MessageId); // already pinned

        await ctx.BuildSut().PinMessageAsync(ActorId, GuildId, ChannelId, MessageId);

        ctx.Messages.Verify(m => m.PinAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        ctx.Broadcaster.Verify(b => b.BroadcastMessagePinnedAsync(It.IsAny<MessagePinPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DmPin_Participant_Pins_NoAuditNoSystemNotice()
    {
        var ctx = new Ctx();
        ctx.SetUpDm(participant: true);
        ctx.SetUpPins();

        await ctx.BuildSut().PinMessageAsync(ActorId, guildId: null, ChannelId, MessageId);

        ctx.Messages.Verify(m => m.PinAsync(ChannelId, MessageId, ActorId, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Broadcaster.Verify(b => b.BroadcastMessagePinnedAsync(It.IsAny<MessagePinPayload>(), It.IsAny<CancellationToken>()), Times.Once);
        // DMs have no audit log and no system-message infra.
        ctx.AuditLog.Verify(a => a.LogAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long?>(),
            It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        ctx.Publisher.Verify(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DmPin_NonParticipant_Throws403()
    {
        var ctx = new Ctx();
        ctx.SetUpDm(participant: false);

        var act = () => ctx.BuildSut().PinMessageAsync(ActorId, guildId: null, ChannelId, MessageId);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ctx.Messages.Verify(m => m.PinAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GuildUnpin_WithPermission_UnpinsBroadcastsAndAudits()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canPin: true);

        await ctx.BuildSut().UnpinMessageAsync(ActorId, GuildId, ChannelId, MessageId);

        ctx.Messages.Verify(m => m.UnpinAsync(ChannelId, MessageId, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Broadcaster.Verify(b => b.BroadcastMessageUnpinnedAsync(
            It.Is<MessagePinPayload>(p => p.MessageId == MessageId && p.ChannelId == ChannelId),
            It.IsAny<CancellationToken>()), Times.Once);
        ctx.AuditLog.Verify(a => a.LogAsync(
            GuildId, ActorId, AuditLogAction.MessageUnpin, MessageId,
            It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
