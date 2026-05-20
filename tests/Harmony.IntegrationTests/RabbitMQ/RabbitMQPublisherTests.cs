using FluentAssertions;
using Harmony.Core.Interfaces;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.RabbitMQ.Producers;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Harmony.IntegrationTests.RabbitMQ;

/// <summary>
/// Tests the API side (Producer). It connects to a real RabbitMQ test instance,
/// publishes a message, and proves the message physically arrived in the queue with the correct metadata.
/// </summary>
public class RabbitMQPublisherTests : IAsyncLifetime
{
    private RabbitMQConnection _connection = null!;
    private IMessagePublisher _publisher = null!;
    private IChannel _verifyChannel = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // Connect to a local test RabbitMQ
                    ["ConnectionStrings:RabbitMQ"] = "amqp://admin:secret@localhost:5672",
                }
            )
            .Build();

        _connection = new RabbitMQConnection(config, NullLogger<RabbitMQConnection>.Instance);
        _publisher = new RabbitMQPublisher(_connection, NullLogger<RabbitMQPublisher>.Instance);
        _verifyChannel = await _connection.CreateChannelAsync();
    }

    public async Task DisposeAsync()
    {
        // Purge test queues so other tests running next start with an empty queue
        await _verifyChannel.QueuePurgeAsync(Topology.MessagePersistQueue);
        await _verifyChannel.QueuePurgeAsync(Topology.NotificationQueue);
        await _verifyChannel.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldDeliverToMessagePersistQueue()
    {
        // Arrange
        var evt = BuildMessageSentEvent();

        // Act - Publish event to the main Exchange
        await _publisher.PublishMessageSentAsync(evt);

        // Assert - Use Polly to instantly grab the message once RabbitMQ routes it
        // autoAck: true means we delete it from the queue after checking it
        var result = await Eventually.GetAsync(
            action: () => _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true),
            predicate: res => res is not null
        );

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishMessageDeletedAsync_ShouldDeliverToMessagePersistQueue()
    {
        var evt = new MessageDeletedEvent(2001, 100, 1, 99, DateTimeOffset.UtcNow);

        await _publisher.PublishMessageDeletedAsync(evt);

        var result = await Eventually.GetAsync(
            () => _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true),
            res => res is not null
        );

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishMessageEditedAsync_ShouldDeliverToMessagePersistQueue()
    {
        var evt = new MessageEditedEvent(3001, 100, 1, 99, "edited content", DateTimeOffset.UtcNow);

        await _publisher.PublishMessageEditedAsync(evt);

        var result = await Eventually.GetAsync(
            () => _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true),
            res => res is not null
        );

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldSetPersistentDeliveryMode()
    {
        var evt = BuildMessageSentEvent();

        await _publisher.PublishMessageSentAsync(evt);

        var result = await Eventually.GetAsync(
            () => _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true),
            res => res is not null
        );

        // Assert - Ensure the message survives a RabbitMQ server restart!
        result!.BasicProperties.Persistent.Should().BeTrue();
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldSetCorrectContentType()
    {
        var evt = BuildMessageSentEvent();

        await _publisher.PublishMessageSentAsync(evt);

        var result = await Eventually.GetAsync(
            () => _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true),
            res => res is not null
        );

        // Assert - Ensure standard JSON type so cross-language consumers (like Node/Go) can read it
        result!.BasicProperties.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task PublishMultipleMessages_ShouldAllDeliverToQueue()
    {
        // Arrange - Generate 5 events
        var events = Enumerable
            .Range(1, 5)
            .Select(i => BuildMessageSentEvent(messageId: i * 1000L))
            .ToList();

        // Act - Rapid-fire publish all 5
        foreach (var evt in events)
            await _publisher.PublishMessageSentAsync(evt);

        // Assert - QueueDeclarePassive returns queue statistics without altering the queue!
        // We use Polly to keep asking RabbitMQ for stats until the MessageCount reaches 5.
        var queueInfo = await Eventually.GetAsync(
            action: () => _verifyChannel.QueueDeclarePassiveAsync(Topology.MessagePersistQueue),
            predicate: info => info.MessageCount == 5
        );

        queueInfo.MessageCount.Should().Be(5);
    }

    [Fact]
    public async Task PublishMessageSentAsync_ShouldSetMessageId()
    {
        var evt = BuildMessageSentEvent();

        await _publisher.PublishMessageSentAsync(evt);

        var result = await Eventually.GetAsync(
            () => _verifyChannel.BasicGetAsync(Topology.MessagePersistQueue, autoAck: true),
            res => res is not null
        );

        result!.BasicProperties.MessageId.Should().NotBeNullOrWhiteSpace();
    }

    // --- Helpers ---

    private static MessageSentEvent BuildMessageSentEvent(long messageId = 1001) =>
        new(
            MessageId: messageId,
            ChannelId: 100,
            GuildId: 1,
            UserId: 99,
            Content: "hello world",
            MessageType: "text",
            AttachmentIds: [],
            MentionIds: [],
            ReplyToId: null,
            SentAt: DateTimeOffset.UtcNow
        );
}
