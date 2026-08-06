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
/// Reaction authorization + validation (slice 1): guild reactions require
/// <c>ViewChannel</c>+<c>AddReactions</c>; DM reactions require participation; the emoji is
/// validated (empty / whitespace / oversized / reserved <c>custom:</c> prefix rejected); the target
/// must exist, live in the channel, and not be deleted; a successful add persists and broadcasts
/// <c>ReactionAdded</c>.
/// </summary>
public class MessageServiceReactionTests
{
    private const long GuildId = 100;
    private const long ChannelId = 200;
    private const long MessageId = 300;
    private const long ActorId = 1;
    private const long AuthorId = 2;
    private const string Emoji = "😀";

    private sealed class Ctx
    {
        public Mock<IChannelRepository> Channels { get; } = new();
        public Mock<IMessageRepository> Messages { get; } = new();
        public Mock<IPermissionService> Permissions { get; } = new();
        public Mock<IDirectMessageRepository> Dms { get; } = new();
        public Mock<IMessageReactionRepository> Reactions { get; } = new();
        public Mock<IHubBroadcaster> Broadcaster { get; } = new();

        public MessageService BuildSut() =>
            new(
                Channels.Object, Mock.Of<IGuildRepository>(), Mock.Of<IMessagePublisher>(),
                Mock.Of<ISnowflakeIdGenerator>(), Messages.Object, Mock.Of<IUserRepository>(),
                Permissions.Object, Mock.Of<IFileAttachmentRepository>(), Dms.Object,
                Mock.Of<IUserBlockRepository>(), Mock.Of<IFriendRepository>(), Mock.Of<IPresenceService>(),
                Mock.Of<IAuditLogService>(), Broadcaster.Object, Mock.Of<IRoleRepository>(),
                Mock.Of<ISlowmodeGate>(), Reactions.Object, Mock.Of<IFileStorageService>()
            );

        public void SetUpGuild(bool canReact, bool deleted = false)
        {
            Channels.Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId))
                .ReturnsAsync(new Channel { Id = ChannelId, GuildId = GuildId, Type = "text" });
            var bits = canReact ? (long)(Permission.ViewChannel | Permission.AddReactions) : 0L;
            Permissions.Setup(p => p.ResolveAsync(ActorId, GuildId, ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(bits);
            Messages.Setup(m => m.GetByIdAsync(MessageId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Message { MessageId = MessageId, ChannelId = ChannelId, UserId = AuthorId, IsDeleted = deleted });
        }

        public void SetUpDm(bool participant)
        {
            Channels.Setup(c => c.GetByIdAsync(ChannelId))
                .ReturnsAsync(new Channel { Id = ChannelId, GuildId = null, Type = "dm" });
            Dms.Setup(d => d.IsParticipantAsync(ChannelId, ActorId)).ReturnsAsync(participant);
            Messages.Setup(m => m.GetByIdAsync(MessageId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Message { MessageId = MessageId, ChannelId = ChannelId, UserId = AuthorId });
        }
    }

    [Fact]
    public async Task Add_GuildWithPermission_Persists_AndBroadcasts()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canReact: true);

        await ctx.BuildSut().AddReactionAsync(ActorId, GuildId, ChannelId, MessageId, Emoji);

        ctx.Reactions.Verify(r => r.AddAsync(
            MessageId, ChannelId, Emoji, ActorId, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
        ctx.Broadcaster.Verify(b => b.BroadcastReactionAddedAsync(
            It.Is<ReactionPayload>(p => p.MessageId == MessageId && p.Emoji == Emoji && p.UserId == ActorId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_GuildWithoutAddReactions_Throws_AndDoesNotPersist()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canReact: false);

        var act = () => ctx.BuildSut().AddReactionAsync(ActorId, GuildId, ChannelId, MessageId, Emoji);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ctx.Reactions.Verify(r => r.AddAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Add_DeletedMessage_Throws()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canReact: true, deleted: true);

        var act = () => ctx.BuildSut().AddReactionAsync(ActorId, GuildId, ChannelId, MessageId, Emoji);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("custom:12345")]
    [InlineData("a b")]
    public async Task Add_InvalidEmoji_Throws_BeforeAuthorizing(string emoji)
    {
        var ctx = new Ctx();
        // No setup: validation must fail before any repo/permission call.
        var act = () => ctx.BuildSut().AddReactionAsync(ActorId, GuildId, ChannelId, MessageId, emoji);
        await act.Should().ThrowAsync<ArgumentException>();
        ctx.Permissions.Verify(p => p.ResolveAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Add_DmParticipant_Persists()
    {
        var ctx = new Ctx();
        ctx.SetUpDm(participant: true);

        await ctx.BuildSut().AddReactionAsync(ActorId, guildId: null, ChannelId, MessageId, Emoji);

        ctx.Reactions.Verify(r => r.AddAsync(
            MessageId, ChannelId, Emoji, ActorId, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_DmNonParticipant_Throws()
    {
        var ctx = new Ctx();
        ctx.SetUpDm(participant: false);

        var act = () => ctx.BuildSut().AddReactionAsync(ActorId, guildId: null, ChannelId, MessageId, Emoji);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Remove_Broadcasts_ReactionRemoved()
    {
        var ctx = new Ctx();
        ctx.SetUpGuild(canReact: true);

        await ctx.BuildSut().RemoveReactionAsync(ActorId, GuildId, ChannelId, MessageId, Emoji);

        ctx.Reactions.Verify(r => r.RemoveAsync(
            MessageId, Emoji, ActorId, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Broadcaster.Verify(b => b.BroadcastReactionRemovedAsync(
            It.Is<ReactionPayload>(p => p.MessageId == MessageId && p.Emoji == Emoji),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
