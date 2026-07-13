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
/// Server-verified message forwarding (<c>ForwardMessageAsync</c>). Asserts that the snapshot is
/// built from the AUTHORITATIVE source row (author resolved server-side), that a note-less forward
/// still sends (the snapshot is content), and that a forwarder who cannot see the source is refused
/// — no message is leaked across a permission boundary (NON-NEGOTIABLE #8).
/// </summary>
public class MessageServiceForwardTests
{
    private const long ActorId = 1;
    private const long TargetGuildId = 2;
    private const long TargetChannelId = 3;
    private const long SourceGuildId = 4;
    private const long SourceChannelId = 5;
    private const long SourceMessageId = 500;
    private const long AuthorId = 10;

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
        public Mock<IFriendRepository> Friends { get; } = new();
        public Mock<IPresenceService> Presence { get; } = new();
        public Mock<IAuditLogService> AuditLog { get; } = new();
        public Mock<IRoleRepository> Roles { get; } = new();

        public MessageSentEvent? PublishedEvent { get; private set; }

        public Harness()
        {
            Guilds.Setup(g => g.GetMembersAsync(It.IsAny<long>())).ReturnsAsync(new List<GuildMember>());
            Roles.Setup(r => r.GetByGuildAsync(It.IsAny<long>())).ReturnsAsync(new List<Role>());
            Roles
                .Setup(r => r.GetMemberIdsWithRoleAsync(It.IsAny<long>(), It.IsAny<long>()))
                .ReturnsAsync(new List<long>());
            Guilds.Setup(g => g.GetMemberIdsAsync(It.IsAny<long>())).ReturnsAsync(new List<long> { ActorId });
            Users
                .Setup(u => u.GetByIdsAsync(It.IsAny<IEnumerable<long>>()))
                .ReturnsAsync(new Dictionary<long, User> { [ActorId] = new() { Id = ActorId, UserName = "actor" } });
        }

        public MessageService BuildSut()
        {
            Snowflake.Setup(s => s.NextId()).Returns(999);
            Publisher
                .Setup(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()))
                .Callback<MessageSentEvent, CancellationToken>((evt, _) => PublishedEvent = evt)
                .Returns(Task.CompletedTask);

            return new MessageService(
                Channels.Object, Guilds.Object, Publisher.Object, Snowflake.Object,
                Messages.Object, Users.Object, Permissions.Object, Files.Object,
                Dms.Object, Blocks.Object, Friends.Object, Presence.Object, AuditLog.Object,
                Mock.Of<IHubBroadcaster>(), Roles.Object, Mock.Of<ISlowmodeGate>(),
                Mock.Of<IMessageReactionRepository>()
            );
        }

        /// <summary>Target = a guild channel the actor can view + send in.</summary>
        public void SetUpTarget()
        {
            Channels.Setup(c => c.GetByIdAndGuildIdAsync(TargetChannelId, TargetGuildId))
                .ReturnsAsync(new Channel { Id = TargetChannelId, GuildId = TargetGuildId, Type = "text" });
            Permissions.Setup(p => p.ResolveAsync(ActorId, TargetGuildId, TargetChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((long)(Permission.ViewChannel | Permission.SendMessage));
            Guilds.Setup(g => g.GetMemberAsync(TargetGuildId, ActorId))
                .ReturnsAsync(new GuildMember { UserId = ActorId, GuildId = TargetGuildId });
        }

        /// <summary>Source = a guild message authored by <paramref name="authorName"/>.</summary>
        public void SetUpSource(string content, string authorName, bool canView = true, bool deleted = false)
        {
            Messages.Setup(m => m.GetByIdAsync(SourceMessageId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Message
                {
                    MessageId = SourceMessageId,
                    ChannelId = SourceChannelId,
                    UserId = AuthorId,
                    Content = content,
                    IsDeleted = deleted,
                });
            Channels.Setup(c => c.GetByIdAsync(SourceChannelId))
                .ReturnsAsync(new Channel { Id = SourceChannelId, GuildId = SourceGuildId, Type = "text" });
            Permissions.Setup(p => p.HasAsync(ActorId, SourceGuildId, Permission.ViewChannel, SourceChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(canView);
            Users.Setup(u => u.GetByIdAsync(AuthorId))
                .ReturnsAsync(new User { Id = AuthorId, UserName = authorName });
        }
    }

    private static ForwardMessageRequest Req(string? note = null) =>
        new(SourceChannelId, SourceMessageId, note);

    [Fact]
    public async Task ForwardMessageAsync_ShouldStampServerAuthoritativeSnapshot()
    {
        var h = new Harness();
        h.SetUpTarget();
        h.SetUpSource(content: "the original text", authorName: "alice");
        var sut = h.BuildSut();

        await sut.ForwardMessageAsync(ActorId, TargetGuildId, TargetChannelId, Req(note: "look at this"));

        var fwd = h.PublishedEvent!.Forward;
        fwd.Should().NotBeNull();
        fwd!.AuthorId.Should().Be(AuthorId);
        fwd.AuthorName.Should().Be("alice");
        fwd.Content.Should().Be("the original text");
        // The forwarder's note becomes the message content; the snapshot is separate.
        h.PublishedEvent!.Content.Should().Be("look at this");
    }

    [Fact]
    public async Task ForwardMessageAsync_ShouldSend_EvenWithNoNoteOrAttachments()
    {
        // A note-less forward has empty content and no attachments — the snapshot satisfies the
        // "content or attachment required" gate, so it must still publish.
        var h = new Harness();
        h.SetUpTarget();
        h.SetUpSource(content: "hi", authorName: "alice");
        var sut = h.BuildSut();

        await sut.ForwardMessageAsync(ActorId, TargetGuildId, TargetChannelId, Req());

        h.PublishedEvent.Should().NotBeNull();
        h.PublishedEvent!.Content.Should().BeEmpty();
        h.PublishedEvent!.Forward!.Content.Should().Be("hi");
    }

    [Fact]
    public async Task ForwardMessageAsync_ShouldRefuse_WhenForwarderCannotSeeSource()
    {
        var h = new Harness();
        h.SetUpTarget();
        h.SetUpSource(content: "secret", authorName: "alice", canView: false);
        var sut = h.BuildSut();

        var act = () => sut.ForwardMessageAsync(ActorId, TargetGuildId, TargetChannelId, Req());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        h.PublishedEvent.Should().BeNull();
    }

    [Fact]
    public async Task ForwardMessageAsync_ShouldRefuse_WhenSourceDeleted()
    {
        var h = new Harness();
        h.SetUpTarget();
        h.SetUpSource(content: "gone", authorName: "alice", deleted: true);
        var sut = h.BuildSut();

        var act = () => sut.ForwardMessageAsync(ActorId, TargetGuildId, TargetChannelId, Req());

        await act.Should().ThrowAsync<KeyNotFoundException>();
        h.PublishedEvent.Should().BeNull();
    }
}
