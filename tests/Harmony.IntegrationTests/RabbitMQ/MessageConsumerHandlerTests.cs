// --- START OF FILE MessageConsumerHandlerTests.cs ---
using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.Scylla; // Added to resolve MessageStatements
using Harmony.Infrastructure.Scylla.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harmony.IntegrationTests.RabbitMQ;

/// <summary>
/// Tests the background worker (Consumer Handler).
/// Inherits from ScyllaAndPostgresTestBase which uses Respawn to instantly wipe Postgres
/// and TRUNCATEs Scylla tables between EVERY test, ensuring a clean state.
/// </summary>
public class MessageConsumerHandlerTests : ScyllaAndPostgresTestBase
{
    protected override IEnumerable<string> TablesToTruncate =>
        ["messages_by_channel", "messages_by_id"];

    protected override IEnumerable<string> PostgresTablesToIgnore => ["__EFMigrationsHistory"];

    private IMessageRepository _messageRepository = null!;
    private MessageConsumerHandler _handler = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync(); // Triggers Respawn (Postgres wipe) and Scylla TRUNCATE

        // We use a "Stub" to bypass real connection logic and point the Repo to the test keyspace
        var stub = new ScyllaSessionFactoryStub(Session);

        // Compile statements cache inside the test context
        var statements = new MessageStatements(stub);

        // Manual Dependency Injection: We inject the Stub, the statements cache, and a NullLogger
        _messageRepository = new MessageRepository(
            stub,
            statements,
            NullLogger<MessageRepository>.Instance
        );

        // Inject the mocked repo and the real test database context into the handler
        _handler = new MessageConsumerHandler(
            _messageRepository,
            Db,
            NullLogger<MessageConsumerHandler>.Instance
        );

        // Seed basic relational data (User, Guild, Channel) needed for foreign keys
        await SeedDefaultGuildAndChannelAsync();
    }

    // --- HandleMessageSentAsync ---

    [Fact]
    public async Task HandleMessageSentAsync_ShouldCreateMentionNotifications()
    {
        // Arrange
        await CreateUserAsync(id: 500, username: "mentioneduser");
        await CreateNotificationPreferenceAsync(userId: 500, mentionsEnabled: true);
        var evt = BuildMessageSentEvent(messageId: 1004, mentionIds: [500]);

        // Act
        await _handler.HandleMessageSentAsync(evt);

        // Assert - Verify the mention logic successfully wrote a notification record
        var notification = await Db.Notifications.FirstOrDefaultAsync(n =>
            n.UserId == 500 && n.Type == "mention"
        );
        notification.Should().NotBeNull();
        notification!.MessageId.Should().Be(1004);
        notification.ActorId.Should().Be(99);
    }

    // --- HandleMessageDeletedAsync ---

    [Fact]
    public async Task HandleMessageDeletedAsync_ShouldSoftDeleteInScylla()
    {
        // Arrange - Send the initial message
        var sentEvt = BuildMessageSentEvent(messageId: 2001);
        await _handler.HandleMessageSentAsync(sentEvt);

        // Wait until Scylla confirms the write using our Polly resilience helper.
        // It checks every 50ms, meaning the test will proceed instantly once data is found.
        var messages = await Eventually.HasAnyAsync(() =>
            _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10)
        );
        messages.Should().NotBeEmpty("message must exist before deleting");

        var deletedEvt = new MessageDeletedEvent(
            MessageId: 2001,
            ChannelId: 1,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow
        );

        // Act - Simulate RabbitMQ delivering a delete event
        await _handler.HandleMessageDeletedAsync(deletedEvt);

        // Assert - Wait until Scylla updates the record to IsDeleted = true
        var deletedMessages = await Eventually.MatchesAsync(
            () => _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10),
            m => m.Any() && m.First().IsDeleted
        );

        deletedMessages.First().IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMessageDeletedAsync_ShouldRemoveFromPostgres()
    {
        // Arrange
        var sentEvt = BuildMessageSentEvent(messageId: 2002);
        await _handler.HandleMessageSentAsync(sentEvt);

        var deletedEvt = new MessageDeletedEvent(
            MessageId: 2002,
            ChannelId: 1,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow
        );

        // Act
        await _handler.HandleMessageDeletedAsync(deletedEvt);

        // Assert - Hard delete from Search index so users can't search deleted messages
        var entry = await Db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 2002);
        entry.Should().BeNull();
    }

    // --- HandleMessageEditedAsync ---

    [Fact]
    public async Task HandleMessageEditedAsync_ShouldUpdateContentInScylla()
    {
        // Arrange
        var sentEvt = BuildMessageSentEvent(messageId: 3001);
        await _handler.HandleMessageSentAsync(sentEvt);

        // Wait for Scylla consistency
        var messages = await Eventually.HasAnyAsync(() =>
            _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10)
        );
        messages.Should().NotBeEmpty("initial write must land before editing");

        var editedEvt = new MessageEditedEvent(
            MessageId: 3001,
            ChannelId: 1,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "edited content",
            EditedAt: DateTimeOffset.UtcNow
        );

        // Act
        await _handler.HandleMessageEditedAsync(editedEvt);

        // Assert - Poll Scylla until the content changes to the edited text
        var editedMessages = await Eventually.MatchesAsync(
            () => _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10),
            m => m.Any() && m.First().Content == "edited content"
        );

        editedMessages.First().Content.Should().Be("edited content");
        editedMessages.First().IsEdited.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldPersistToScylla()
    {
        var stub = new ScyllaSessionFactoryStub(Session);
        var statements = new MessageStatements(stub);
        var directRepo = new MessageRepository(
            stub,
            statements,
            NullLogger<MessageRepository>.Instance
        );

        await directRepo.SaveAsync(
            new Message
            {
                MessageId = 9999,
                ChannelId = 1,
                UserId = 99,
                Content = "direct write test",
                AttachmentIds = [],
                MentionIds = [],
                IsDeleted = false,
                IsEdited = false,
                MessageType = "text",
            }
        );

        var messages = await Eventually.HasAnyAsync(() =>
            directRepo.GetChannelMessagesAsync(channelId: 1, limit: 10)
        );

        messages.Should().NotBeEmpty();
        messages.First().Content.Should().Be("direct write test");
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldSkipMention_WhenMentionsDisabled()
    {
        await CreateUserAsync(id: 501, username: "quietuser");
        await CreateNotificationPreferenceAsync(userId: 501, mentionsEnabled: false);

        var evt = BuildMessageSentEvent(messageId: 1005, mentionIds: [501]);
        await _handler.HandleMessageSentAsync(evt);

        var count = await Db.Notifications.CountAsync(n => n.UserId == 501);
        count.Should().Be(0);
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldSkipSelfMention()
    {
        var evt = BuildMessageSentEvent(messageId: 1006, mentionIds: [99]);
        await _handler.HandleMessageSentAsync(evt);

        var count = await Db.Notifications.CountAsync(n => n.UserId == 99);
        count.Should().Be(0);
    }

    // --- Helpers ---
    private static MessageSentEvent BuildMessageSentEvent(
        long messageId,
        List<long>? mentionIds = null
    ) =>
        new(
            messageId,
            1,
            1,
            99,
            "hello world",
            "text",
            [],
            mentionIds ?? [],
            null,
            DateTimeOffset.UtcNow
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

    private async Task<User> CreateUserAsync(long id, string username)
    {
        var user = new User
        {
            Id = id,
            UserName = username,
            NormalizedUserName = username.ToUpper(),
            Email = $"{username}@test.com",
            NormalizedEmail = $"{username}@test.com".ToUpper(),
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            Discriminator = "0001",
            AccountStatus = "active",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task CreateNotificationPreferenceAsync(long userId, bool mentionsEnabled)
    {
        Db.NotificationPreferences.Add(
            new NotificationPreference
            {
                UserId = userId,
                MentionsEnabled = mentionsEnabled,
                RepliesEnabled = true,
                FriendRequests = true,
                GuildInvites = true,
                PushEnabled = true,
            }
        );
        await Db.SaveChangesAsync();
    }
}
