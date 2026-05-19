using FluentAssertions;
using Harmony.Core.Interfaces;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.RabbitMQ.Producers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Harmony.IntegrationTests.RabbitMQ;

public class RabbitMQPublisherTests : IAsyncLifetime
{
    private RabbitMQConnection _connection = null!;
    private IMessagePublisher _publisher = null!;
    private IChannel _verifyChannel = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RabbitMQ"] = "amqp://admin:secret@localhost:5672"
            })
            .Build();

        _connection = new RabbitMQConnection(config, NullLogger<RabbitMQConnection>.Instance);
        _publisher = new RabbitMQPublisher(_connection, NullLogger<RabbitMQPublisher>.Instance);
        _verifyChannel = await _connection.CreateChannelAsync();
    }

    public async Task DisposeAsync()
    {
        // Purge test queues so other tests start clean
        await _verifyChannel.QueuePurgeAsync(Topology.MessagePersistQueue);
        await _verifyChannel.QueuePurgeAsync(Topology.NotificationQueue);
        await _verifyChannel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldDeliverToMessagePersistQueue()
    {
        var evt = BuildMessageSentEvent();

        await _publisher.PublishMessageSentAsync(evt);

        await Task.Delay(300); // let RabbitMQ route the message

        var result = await _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishMessageDeletedAsync_ShouldDeliverToMessagePersistQueue()
    {
        var evt = new MessageDeletedEvent(
            MessageId: 2001,
            ChannelId: 100,
            GuildId: 1,
            DeletedByUserId: 99,
            DeletedAt: DateTimeOffset.UtcNow);

        await _publisher.PublishMessageDeletedAsync(evt);

        await Task.Delay(300);

        var result = await _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishMessageEditedAsync_ShouldDeliverToMessagePersistQueue()
    {
        var evt = new MessageEditedEvent(
            MessageId: 3001,
            ChannelId: 100,
            GuildId: 1,
            EditedByUserId: 99,
            NewContent: "edited content",
            EditedAt: DateTimeOffset.UtcNow);

        await _publisher.PublishMessageEditedAsync(evt);

        await Task.Delay(300);

        var result = await _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldSetPersistentDeliveryMode()
    {
        var evt = BuildMessageSentEvent();

        await _publisher.PublishMessageSentAsync(evt);

        await Task.Delay(300);

        var result = await _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true);
        result.Should().NotBeNull();
        result!.BasicProperties.Persistent.Should().BeTrue();
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldSetCorrectContentType()
    {
        var evt = BuildMessageSentEvent();

        await _publisher.PublishMessageSentAsync(evt);

        await Task.Delay(300);

        var result = await _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true);
        result.Should().NotBeNull();
        result!.BasicProperties.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldSetMessageId()
    {
        var evt = BuildMessageSentEvent();

        await _publisher.PublishMessageSentAsync(evt);

        await Task.Delay(300);

        var result = await _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true);
        result.Should().NotBeNull();
        result!.BasicProperties.MessageId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PublishMultipleMessages_ShouldAllDeliverToQueue()
    {
        var events = Enumerable.Range(1, 5)
            .Select(i => BuildMessageSentEvent(messageId: i * 1000L))
            .ToList();

        foreach (var evt in events)
            await _publisher.PublishMessageSentAsync(evt);

        await Task.Delay(500);

        var count = 0;
        while (true)
        {
            var result = await _verifyChannel.BasicGetAsync(
                Topology.MessagePersistQueue, autoAck: true);
            if (result is null) break;
            count++;
        }

        count.Should().Be(5);
    }

    // --- Helpers ---

    private static MessageSentEvent BuildMessageSentEvent(long messageId = 1001) => new(
        MessageId: messageId,
        ChannelId: 100,
        GuildId: 1,
        UserId: 99,
        Content: "hello world",
        MessageType: "text",
        AttachmentIds: [],
        MentionIds: [],
        ReplyToId: null,
        SentAt: DateTimeOffset.UtcNow);
}