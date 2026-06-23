using FluentAssertions;
using Harmony.Application.Exceptions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories; // Required namespace
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.Scylla;
using Harmony.Infrastructure.Scylla.Repositories; // Required namespace
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
    private IMessageRepository _messageRepository = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Instantiate primary storage repository stub for testing
        var stub = new ScyllaSessionFactoryStub(Session);
        var statements = new MessageStatements(stub);
        _messageRepository = new MessageRepository(
            stub,
            statements,
            NullLogger<MessageRepository>.Instance
        );

        _handler = new SearchIndexConsumerHandler(
            Db,
            _messageRepository, // Pass Scylla repository
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

        // Pre-populate ScyllaDB with the sent message so the handler knows the
        // deleted message was a completed deletion (idempotent) and not out-of-order write lag
        var scyllaMessage = new Message
        {
            MessageId = 2002,
            ChannelId = 1,
            UserId = 99,
            Content = "hello world",
            IsDeleted = true,
            MessageType = "text",
        };
        await _messageRepository.SaveAsync(scyllaMessage);

        var deletedEvt = new MessageDeletedEvent(
            MessageId: 2002,
            ChannelId: 1,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageDeletedAsync(deletedEvt);

        // This second delete execution will query ScyllaDB, find the deleted message,
        // and return success idempotently instead of throwing a requeuing exception
        var act = () => _handler.HandleMessageDeletedAsync(deletedEvt);
        await act.Should().NotThrowAsync();

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
            MentionIds: [],
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
            MentionIds: [],
            EditedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageEditedAsync(editedEvt);
        await _handler.HandleMessageEditedAsync(editedEvt);

        var entry = await Db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 3002);
        entry!.Content.Should().Be("final content");
    }

    [Fact]
    public async Task HandleMessageEditedAsync_WhenMessageNotFoundInIndex_ShouldThrowServiceUnavailableException()
    {
        // Arrange: Build an edit event targeting a message ID that does not exist in Postgres FTS or Scylla yet
        var editedEvt = new MessageEditedEvent(
            MessageId: 999999L,
            ChannelId: 1,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "should requeue",
            MentionIds: [],
            EditedAt: DateTimeOffset.UtcNow
        );

        // Act: Invoke the handler directly
        var act = () => _handler.HandleMessageEditedAsync(editedEvt);

        // Assert: The handler must throw ServiceUnavailableException to trigger RabbitMQ requeue backoff
        await act.Should().ThrowAsync<ServiceUnavailableException>();
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
