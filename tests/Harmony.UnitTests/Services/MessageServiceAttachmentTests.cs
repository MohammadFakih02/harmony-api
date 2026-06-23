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
/// Attachment validation on the send path. The structural plumbing (ids → event → Scylla → response)
/// already exists; these cover the guard MessageService adds: attachments must exist, be confirmed,
/// be owned by the sender, and belong to the target channel — plus the content-or-attachment rule.
/// </summary>
public class MessageServiceAttachmentTests
{
    private const long UserId = 1;
    private const long GuildId = 2;
    private const long ChannelId = 3;
    private const long AttachmentId = 4;

    private static (MessageService sut, Mock<IMessagePublisher> publisher, Mock<IFileAttachmentRepository> files)
        BuildSut()
    {
        var channels = new Mock<IChannelRepository>();
        var guilds = new Mock<IGuildRepository>();
        var publisher = new Mock<IMessagePublisher>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();
        var messages = new Mock<IMessageRepository>();
        var users = new Mock<IUserRepository>();
        var permissions = new Mock<IPermissionService>();
        var files = new Mock<IFileAttachmentRepository>();
        var dms = new Mock<IDirectMessageRepository>();
        var blocks = new Mock<IUserBlockRepository>();
        var presence = new Mock<IPresenceService>();

        // Happy-path send context: channel exists, can view+send, not timed out.
        channels.Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId))
            .ReturnsAsync(new Channel { Id = ChannelId, GuildId = GuildId });
        permissions.Setup(p => p.ResolveAsync(UserId, GuildId, ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((long)(Permission.ViewChannel | Permission.SendMessage));
        guilds.Setup(g => g.GetMemberAsync(GuildId, UserId))
            .ReturnsAsync(new GuildMember { UserId = UserId, GuildId = GuildId });
        // Mention resolution candidate scoping — no mentioned users in these attachment-focused tests.
        guilds.Setup(g => g.GetMemberIdsAsync(GuildId)).ReturnsAsync(new List<long> { UserId });
        users.Setup(u => u.GetByIdsAsync(It.IsAny<IEnumerable<long>>()))
            .ReturnsAsync(new Dictionary<long, User>());
        snowflake.Setup(s => s.NextId()).Returns(999);
        publisher.Setup(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new MessageService(
            channels.Object, guilds.Object, publisher.Object, snowflake.Object,
            messages.Object, users.Object, permissions.Object, files.Object,
            dms.Object, blocks.Object, presence.Object
        );
        return (sut, publisher, files);
    }

    private static FileAttachment ValidAttachment() =>
        new()
        {
            Id = AttachmentId,
            UploaderId = UserId,
            GuildId = GuildId,
            ChannelId = ChannelId,
            MinioKey = "attachments/2/3/4",
            Filename = "pic.png",
            ContentType = "image/png",
            SizeBytes = 1024,
            IsConfirmed = true,
        };

    private static SendMessageRequest Request(string content, params long[] attachmentIds) =>
        new(Content: content, AttachmentIds: attachmentIds.ToList());

    [Fact]
    public async Task Send_WithValidAttachment_Publishes()
    {
        var (sut, publisher, files) = BuildSut();
        files.Setup(f => f.GetByIdAsync(AttachmentId)).ReturnsAsync(ValidAttachment());

        var result = await sut.SendMessageAsync(UserId, GuildId, ChannelId, Request("hi", AttachmentId));

        result.AttachmentIds.Should().ContainSingle().Which.Should().Be(AttachmentId);
        publisher.Verify(p => p.PublishMessageSentAsync(
            It.Is<MessageSentEvent>(e => e.AttachmentIds.Contains(AttachmentId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_ImageOnly_EmptyContentWithAttachment_Succeeds()
    {
        var (sut, publisher, files) = BuildSut();
        files.Setup(f => f.GetByIdAsync(AttachmentId)).ReturnsAsync(ValidAttachment());

        var result = await sut.SendMessageAsync(UserId, GuildId, ChannelId, Request("", AttachmentId));

        result.Content.Should().BeEmpty();
        result.AttachmentIds.Should().ContainSingle();
        publisher.Verify(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_EmptyContent_NoAttachment_ThrowsArgument()
    {
        var (sut, publisher, _) = BuildSut();

        await sut.Invoking(s => s.SendMessageAsync(UserId, GuildId, ChannelId, Request("")))
            .Should().ThrowAsync<ArgumentException>();
        publisher.Verify(p => p.PublishMessageSentAsync(It.IsAny<MessageSentEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_AttachmentNotOwnedBySender_ThrowsUnauthorized()
    {
        var (sut, _, files) = BuildSut();
        var other = ValidAttachment();
        other.UploaderId = UserId + 1;
        files.Setup(f => f.GetByIdAsync(AttachmentId)).ReturnsAsync(other);

        await sut.Invoking(s => s.SendMessageAsync(UserId, GuildId, ChannelId, Request("hi", AttachmentId)))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Send_UnconfirmedAttachment_ThrowsArgument()
    {
        var (sut, _, files) = BuildSut();
        var pending = ValidAttachment();
        pending.IsConfirmed = false;
        files.Setup(f => f.GetByIdAsync(AttachmentId)).ReturnsAsync(pending);

        await sut.Invoking(s => s.SendMessageAsync(UserId, GuildId, ChannelId, Request("hi", AttachmentId)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Send_AttachmentFromDifferentChannel_ThrowsArgument()
    {
        var (sut, _, files) = BuildSut();
        var elsewhere = ValidAttachment();
        elsewhere.ChannelId = ChannelId + 1;
        files.Setup(f => f.GetByIdAsync(AttachmentId)).ReturnsAsync(elsewhere);

        await sut.Invoking(s => s.SendMessageAsync(UserId, GuildId, ChannelId, Request("hi", AttachmentId)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Send_UnknownAttachment_ThrowsArgument()
    {
        var (sut, _, files) = BuildSut();
        files.Setup(f => f.GetByIdAsync(AttachmentId)).ReturnsAsync((FileAttachment?)null);

        await sut.Invoking(s => s.SendMessageAsync(UserId, GuildId, ChannelId, Request("hi", AttachmentId)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Send_TooManyAttachments_ThrowsArgument()
    {
        var (sut, _, _) = BuildSut();
        var tooMany = Enumerable.Range(1, MessageService.MaxAttachments + 1).Select(i => (long)i).ToArray();

        await sut.Invoking(s => s.SendMessageAsync(UserId, GuildId, ChannelId, Request("hi", tooMany)))
            .Should().ThrowAsync<ArgumentException>();
    }
}
