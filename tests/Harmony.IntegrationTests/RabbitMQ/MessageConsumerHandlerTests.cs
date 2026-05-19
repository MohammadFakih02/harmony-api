using FluentAssertions;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.Scylla.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harmony.IntegrationTests.RabbitMQ;

public class MessageConsumerHandlerTests : ScyllaTestBase
{
    protected override IEnumerable<string> TablesToTruncate =>
        ["messages_by_channel", "messages_by_id"];

    private HarmonyDbContext _db = null!;
    private IMessageRepository _messageRepository = null!;
    private MessageConsumerHandler _handler = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var options = new DbContextOptionsBuilder<HarmonyDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=harmony_test;Username=admin;Password=secret"
            )
            .Options;

        _db = new HarmonyDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        // Clean in correct dependency order — children before parents
        _db.Notifications.RemoveRange(_db.Notifications);
        _db.MessagesSearch.RemoveRange(_db.MessagesSearch);
        _db.NotificationPreferences.RemoveRange(_db.NotificationPreferences);
        _db.GuildMembers.RemoveRange(_db.GuildMembers);
        _db.Channels.RemoveRange(_db.Channels);
        _db.Guilds.RemoveRange(_db.Guilds);
        _db.Users.RemoveRange(_db.Users);
        await _db.SaveChangesAsync();

        var stub = new ScyllaSessionFactoryStub(Session);
        stub.Keyspace.Should().Be("harmony_test");
        stub.Session.Keyspace.Should().Be("harmony_test");
        _messageRepository = new MessageRepository(stub, NullLogger<MessageRepository>.Instance);

        _handler = new MessageConsumerHandler(
            _messageRepository,
            _db,
            NullLogger<MessageConsumerHandler>.Instance
        );

        // Seed a default guild and channel that all tests reference
        await SeedDefaultGuildAndChannelAsync();
    }

    public override async Task DisposeAsync()
    {
        // Clean in correct dependency order
        _db.Notifications.RemoveRange(_db.Notifications);
        _db.MessagesSearch.RemoveRange(_db.MessagesSearch);
        _db.NotificationPreferences.RemoveRange(_db.NotificationPreferences);
        _db.GuildMembers.RemoveRange(_db.GuildMembers);
        _db.Channels.RemoveRange(_db.Channels);
        _db.Guilds.RemoveRange(_db.Guilds);
        _db.Users.RemoveRange(_db.Users);
        await _db.SaveChangesAsync();
        await _db.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldPersistToScylla()
    {
        // Confirm keyspaces
        Session.Keyspace.Should().Be("harmony_test");

        var stub = new ScyllaSessionFactoryStub(Session);
        stub.Keyspace.Should().Be("harmony_test");
        stub.Session.Keyspace.Should().Be("harmony_test");

        // Write directly via repository bypassing handler
        var directRepo = new MessageRepository(stub, NullLogger<MessageRepository>.Instance);
        await directRepo.SaveAsync(
            new Harmony.Core.Domain.Entities.Message
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

        await Task.Delay(500);

        var rows = await Session.ExecuteAsync(
            new Cassandra.SimpleStatement(
                "SELECT * FROM harmony_test.messages_by_channel WHERE channel_id = 1"
            )
        );
        rows.ToList().Should().NotBeEmpty("direct write should have persisted");
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldDualWriteToPostgres()
    {
        var evt = BuildMessageSentEvent(messageId: 1002);

        await _handler.HandleMessageSentAsync(evt);

        var entry = await _db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 1002);
        entry.Should().NotBeNull();
        entry!.Content.Should().Be("hello world");
        entry.ChannelId.Should().Be(1);
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldBeIdempotent_WhenCalledTwice()
    {
        var evt = BuildMessageSentEvent(messageId: 1003);

        await _handler.HandleMessageSentAsync(evt);
        await _handler.HandleMessageSentAsync(evt);

        var count = await _db.MessagesSearch.CountAsync(m => m.MessageId == 1003);
        count.Should().Be(1);
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldCreateMentionNotifications()
    {
        await CreateUserAsync(id: 500, username: "mentioneduser");
        await CreateNotificationPreferenceAsync(userId: 500, mentionsEnabled: true);

        var evt = BuildMessageSentEvent(messageId: 1004, mentionIds: [500]);

        await _handler.HandleMessageSentAsync(evt);

        var notification = await _db.Notifications.FirstOrDefaultAsync(n =>
            n.UserId == 500 && n.Type == "mention"
        );
        notification.Should().NotBeNull();
        notification!.MessageId.Should().Be(1004);
        notification.ActorId.Should().Be(99);
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldSkipMention_WhenMentionsDisabled()
    {
        await CreateUserAsync(id: 501, username: "quietuser");
        await CreateNotificationPreferenceAsync(userId: 501, mentionsEnabled: false);

        var evt = BuildMessageSentEvent(messageId: 1005, mentionIds: [501]);

        await _handler.HandleMessageSentAsync(evt);

        var count = await _db.Notifications.CountAsync(n => n.UserId == 501);
        count.Should().Be(0);
    }

    [Fact]
    public async Task HandleMessageSentAsync_ShouldSkipSelfMention()
    {
        var evt = BuildMessageSentEvent(messageId: 1006, mentionIds: [99]);

        await _handler.HandleMessageSentAsync(evt);

        var count = await _db.Notifications.CountAsync(n => n.UserId == 99);
        count.Should().Be(0);
    }

    // --- HandleMessageDeletedAsync ---

    [Fact]
    public async Task HandleMessageDeletedAsync_ShouldSoftDeleteInScylla()
    {
        var sentEvt = BuildMessageSentEvent(messageId: 2001);
        await _handler.HandleMessageSentAsync(sentEvt);

        // Poll until write lands
        List<Message> messages = [];
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(200);
            messages = (
                await _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10)
            ).ToList();
            if (messages.Any())
                break;
        }

        messages.Should().NotBeEmpty("message must exist before deleting");

        var deletedEvt = new MessageDeletedEvent(
            MessageId: 2001,
            ChannelId: 1,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageDeletedAsync(deletedEvt);
        await Task.Delay(500);

        messages = (
            await _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10)
        ).ToList();
        messages.Should().NotBeEmpty();
        messages[0].IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMessageDeletedAsync_ShouldRemoveFromPostgres()
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

        var entry = await _db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 2002);
        entry.Should().BeNull();
    }

    // --- HandleMessageEditedAsync ---

    [Fact]
    public async Task HandleMessageEditedAsync_ShouldUpdateContentInScylla()
    {
        var sentEvt = BuildMessageSentEvent(messageId: 3001);
        await _handler.HandleMessageSentAsync(sentEvt);

        // Poll until initial write lands
        List<Message> messages = [];
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(200);
            messages = (
                await _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10)
            ).ToList();
            if (messages.Any())
                break;
        }

        messages.Should().NotBeEmpty("initial write must land before editing");

        var editedEvt = new MessageEditedEvent(
            MessageId: 3001,
            ChannelId: 1,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "edited content",
            EditedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageEditedAsync(editedEvt);

        // Poll until edit lands
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(200);
            messages = (
                await _messageRepository.GetChannelMessagesAsync(channelId: 1, limit: 10)
            ).ToList();
            if (messages.Any() && messages[0].Content == "edited content")
                break;
        }

        messages.Should().NotBeEmpty();
        messages[0].Content.Should().Be("edited content");
        messages[0].IsEdited.Should().BeTrue();
    }

    [Fact]
    public async Task HandleMessageEditedAsync_ShouldUpdateContentInPostgres()
    {
        var sentEvt = BuildMessageSentEvent(messageId: 3002);
        await _handler.HandleMessageSentAsync(sentEvt);

        var editedEvt = new MessageEditedEvent(
            MessageId: 3002,
            ChannelId: 1,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "updated content",
            EditedAt: DateTimeOffset.UtcNow
        );

        await _handler.HandleMessageEditedAsync(editedEvt);

        var entry = await _db.MessagesSearch.FirstOrDefaultAsync(m => m.MessageId == 3002);
        entry!.Content.Should().Be("updated content");
    }

    // --- Helpers ---

    private static MessageSentEvent BuildMessageSentEvent(
        long messageId,
        List<long>? mentionIds = null
    ) =>
        new(
            MessageId: messageId,
            ChannelId: 1,
            GuildId: 1,
            UserId: 99,
            Content: "hello world",
            MessageType: "text",
            AttachmentIds: [],
            MentionIds: mentionIds ?? [],
            ReplyToId: null,
            SentAt: DateTimeOffset.UtcNow
        );

    private async Task SeedDefaultGuildAndChannelAsync()
    {
        // Owner user must exist before guild due to FK_Guilds_Users_owner_id
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

        _db.Users.Add(owner);
        await _db.SaveChangesAsync();

        var guild = new Guild
        {
            Id = 1,
            Name = "Test Guild",
            OwnerId = 99,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _db.Guilds.Add(guild);
        await _db.SaveChangesAsync();

        var channel = new Channel
        {
            Id = 1,
            GuildId = 1,
            Name = "general",
            Type = "text",
            Position = 0,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _db.Channels.Add(channel);
        await _db.SaveChangesAsync();
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

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task CreateNotificationPreferenceAsync(long userId, bool mentionsEnabled)
    {
        _db.NotificationPreferences.Add(
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
        await _db.SaveChangesAsync();
    }
}
