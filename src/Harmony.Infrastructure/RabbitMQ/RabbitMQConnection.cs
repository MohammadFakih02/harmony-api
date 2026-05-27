using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Harmony.Infrastructure.RabbitMQ;

public class RabbitMQConnection : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly ILogger<RabbitMQConnection> _logger;

    public RabbitMQConnection(IConfiguration configuration, ILogger<RabbitMQConnection> logger)
    {
        _logger = logger;

        var connectionString =
            configuration.GetConnectionString("RabbitMQ") ?? "amqp://guest:guest@localhost:5672";

        var factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
        };

        _logger.LogInformation("Connecting to RabbitMQ at {Uri}", connectionString);
        _connection = factory.CreateConnectionAsync().GetAwaiter().GetResult();
        _logger.LogInformation("RabbitMQ connection established");

        DeclareTopologyAsync().GetAwaiter().GetResult();
    }

    public async Task<IChannel> CreateChannelAsync() => await _connection.CreateChannelAsync();

    private async Task DeclareTopologyAsync()
    {
        using var channel = await _connection.CreateChannelAsync();

        // Dead letter exchange — catches permanently failed messages
        await channel.ExchangeDeclareAsync(
            exchange: Topology.DeadLetterExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false
        );

        await channel.QueueDeclareAsync(
            queue: Topology.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false
        );

        await channel.QueueBindAsync(
            queue: Topology.DeadLetterQueue,
            exchange: Topology.DeadLetterExchange,
            routingKey: "#"
        );

        // Message exchange
        await channel.ExchangeDeclareAsync(
            exchange: Topology.MessageExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false
        );

        // ScyllaDB main queue
        // x-dead-letter-exchange routes nacked messages to DLX retry queue
        await channel.QueueDeclareAsync(
            queue: Topology.ScyllaMessageQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = Topology.ScyllaRetryQueue,
            }
        );

        await channel.QueueBindAsync(
            queue: Topology.ScyllaMessageQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.MessageSentKey
        );

        await channel.QueueBindAsync(
            queue: Topology.ScyllaMessageQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.MessageDeletedKey
        );

        await channel.QueueBindAsync(
            queue: Topology.ScyllaMessageQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.MessageEditedKey
        );

        // ScyllaDB retry queue — passive parking lot, no consumers
        // Messages sit here for RetryTtlMs then auto-republish back to MessageExchange
        await channel.QueueDeclareAsync(
            queue: Topology.ScyllaRetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = Topology.RetryTtlMs,
                ["x-dead-letter-exchange"] = Topology.MessageExchange,
                ["x-dead-letter-routing-key"] = Topology.MessageSentKey,
            }
        );

        // Search index main queue
        await channel.QueueDeclareAsync(
            queue: Topology.SearchIndexQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = Topology.SearchRetryQueue,
            }
        );

        await channel.QueueBindAsync(
            queue: Topology.SearchIndexQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.MessageSentKey
        );

        await channel.QueueBindAsync(
            queue: Topology.SearchIndexQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.MessageDeletedKey
        );

        await channel.QueueBindAsync(
            queue: Topology.SearchIndexQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.MessageEditedKey
        );

        // Search index retry queue — passive parking lot
        await channel.QueueDeclareAsync(
            queue: Topology.SearchRetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = Topology.RetryTtlMs,
                ["x-dead-letter-exchange"] = Topology.MessageExchange,
                ["x-dead-letter-routing-key"] = Topology.MessageSentKey,
            }
        );

        // Notification exchange
        await channel.ExchangeDeclareAsync(
            exchange: Topology.NotificationExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false
        );

        await channel.QueueDeclareAsync(
            queue: Topology.NotificationQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                ["x-message-ttl"] = 86_400_000,
            }
        );

        await channel.QueueBindAsync(
            queue: Topology.NotificationQueue,
            exchange: Topology.NotificationExchange,
            routingKey: Topology.NotificationKey
        );

        _logger.LogInformation("RabbitMQ topology declared");
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
