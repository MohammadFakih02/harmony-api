using FluentAssertions;
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
/// The MessageDelete audit producer (flow #12): a *moderator* deleting someone else's guild
/// message writes one audit entry; deleting your own message writes nothing. Verifies the live
/// wiring added to MessageService.DeleteMessageAsync.
/// </summary>
public class MessageServiceDeleteAuditTests
{
    private const long GuildId = 100;
    private const long ChannelId = 200;
    private const long MessageId = 300;
    private const long ModeratorId = 1;
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

        public MessageService BuildSut() =>
            new(
                Channels.Object, Guilds.Object, Publisher.Object, Snowflake.Object,
                Messages.Object, Users.Object, Permissions.Object, Files.Object,
                Dms.Object, Blocks.Object, Mock.Of<IFriendRepository>(), Presence.Object, AuditLog.Object,
                Mock.Of<IHubBroadcaster>(), Mock.Of<IRoleRepository>()
            );

        public void SetUpGuildDelete(long messageAuthorId, bool canManageMessages)
        {
            Channels.Setup(c => c.GetByIdAndGuildIdAsync(ChannelId, GuildId))
                .ReturnsAsync(new Channel { Id = ChannelId, GuildId = GuildId });
            Messages.Setup(m => m.GetByIdAsync(MessageId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Message { MessageId = MessageId, ChannelId = ChannelId, UserId = messageAuthorId });
            Permissions.Setup(p => p.HasAsync(ModeratorId, GuildId, Permission.ManageMessages, ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(canManageMessages);
        }
    }

    [Fact]
    public async Task ModeratorDeletingAnothersMessage_WritesAuditEntry()
    {
        var ctx = new Ctx();
        ctx.SetUpGuildDelete(messageAuthorId: AuthorId, canManageMessages: true);

        await ctx.BuildSut().DeleteMessageAsync(ModeratorId, GuildId, ChannelId, MessageId);

        ctx.AuditLog.Verify(
            a => a.LogAsync(
                GuildId,
                ModeratorId,
                AuditLogAction.MessageDelete,
                MessageId,
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task DeletingYourOwnMessage_WritesNoAuditEntry()
    {
        var ctx = new Ctx();
        ctx.SetUpGuildDelete(messageAuthorId: ModeratorId, canManageMessages: true);

        await ctx.BuildSut().DeleteMessageAsync(ModeratorId, GuildId, ChannelId, MessageId);

        ctx.AuditLog.Verify(
            a => a.LogAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<long?>(), It.IsAny<object?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }
}
