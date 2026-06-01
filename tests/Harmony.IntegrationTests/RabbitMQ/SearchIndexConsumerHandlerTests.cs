using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Respawn.Graph;

namespace Harmony.IntegrationTests.RabbitMQ;

public class SearchIndexConsumerHandlerTests : ScyllaAndPostgresTestBase
{
    protected override IEnumerable<string> TablesToTruncate => [];

    protected override IEnumerable<string> PostgresTablesToIgnore => ["__EFMigrationsHistory"];

    private SearchIndexConsumerHandler _handler = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _handler = new SearchIndexConsumerHandler(
            Db,
            NullLogger<SearchIndexConsumerHandler>.Instance
        );

        await SeedDefaultGuildAndChannelAsync();
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldIndexToPostgres()
    {
        var evt = BuildMessageSentEvent(messageId: 1001);

        await _handler.HandleMessageSentAsync(evt);

        var entry = await Db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 1001);
        entry.Should().NotBeNull();
        entry!.Content.Should().Be("hello world");
        entry.ChannelId.Should().Be(1);
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldBeIdempotent_WhenCalledTwice()
    {
        var evt = BuildMessageSentEvent(messageId: 1002);

        await _handler.HandleMessageSentAsync(evt);
        await _handler.HandleMessageSentAsync(evt);

        var count = await Db.MessagesSearch.CountAsync(m => m.MessageId == 1002);
        count.Should().Be(1);
    }

    [Fact]
    public async Task HandleMessageDeletedAsync_ShouldRemoveFromIndex()
    {
        var sentEvt = BuildMessageSentEvent(messageId: 2001);
        await _handler.HandleMessageSentAsync(sentEvt);

        var deletedEvt = new MessageDeletedEvent(
            MessageId: 2001,
            ChannelId: 1,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageDeletedAsync(deletedEvt);

        var entry = await Db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 2001);
        entry.Should().BeNull();
    }

    [Fact]
    public async Task HandleMessageDeletedAsync_ShouldBeIdempotent_WhenCalledTwice()
    {
        var sentEvt = BuildMessageSentEvent(messageId: 2002);
        await _handler.HandleMessageSentAsync(sentEvt);

        var deletedEvt = new MessageDeletedEvent(
            MessageId: 2002,
            ChannelId: 1,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageDeletedAsync(deletedEvt);
        await _handler.HandleMessageDeletedAsync(deletedEvt); // second call — should not throw

        var entry = await Db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 2002);
        entry.Should().BeNull();
    }

    [Fact]
    public async Task HandleMessageEditedAsync_ShouldUpdateContentInIndex()
    {
        var sentEvt = BuildMessageSentEvent(messageId: 3001);
        await _handler.HandleMessageSentAsync(sentEvt);

        var editedEvt = new MessageEditedEvent(
            MessageId: 3001,
            ChannelId: 1,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "updated content",
            EditedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageEditedAsync(editedEvt);

        var entry = await Db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 3001);
        entry!.Content.Should().Be("updated content");
    }

    [Fact]
    public async Task HandleMessageEditedAsync_ShouldBeIdempotent_WhenEditedTwice()
    {
        var sentEvt = BuildMessageSentEvent(messageId: 3002);
        await _handler.HandleMessageSentAsync(sentEvt);

        var editedEvt = new MessageEditedEvent(
            MessageId: 3002,
            ChannelId: 1,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "final content",
            EditedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageEditedAsync(editedEvt);
        await _handler.HandleMessageEditedAsync(editedEvt);

        var entry = await Db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 3002);
        entry!.Content.Should().Be("final content");
    }

    // --- Helpers ---

    private static MessageSentEvent BuildMessageSentEvent(long messageId) =>
        new(
            MessageId: messageId,
            ChannelId: 1,
            GuildId: 1,
            UserId: 99,
            Content: "hello world",
            MessageType: "text",
            AttachmentIds: [],
            MentionIds: [],
            ReplyToId: null,
            SentAt: DateTimeOffset.UtcNow
        );

    private async Task SeedDefaultGuildAndChannelAsync()
    {
        var owner = new User
        {
            Id = 99,
            UserName = "testowner",
            NormalizedUserName = "TESTOWNER",
            Email = "owner@test.com",
            NormalizedEmail = "OWNER@TEST.COM",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            Discriminator = "0001",
            AccountStatus = "active",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        Db.Users.Add(owner);
        await Db.SaveChangesAsync();

        Db.Guilds.Add(
            new Guild
            {
                Id = 1,
                Name = "Test Guild",
                OwnerId = 99,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await Db.SaveChangesAsync();

        Db.Channels.Add(
            new Channel
            {
                Id = 1,
                GuildId = 1,
                Name = "general",
                Type = "text",
                Position = 0,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await Db.SaveChangesAsync();
    }
}
