using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Harmony.Infrastructure.RabbitMQ;

public class RabbitMQConnection : IAsyncDisposable
{
    private readonly Lazy<Task<IConnection>> _lazyConnection;
    private readonly ILogger<RabbitMQConnection> _logger;
    private readonly ConnectionFactory _factory;

    public RabbitMQConnection(IConfiguration configuration, ILogger<RabbitMQConnection> logger)
    {
        _logger = logger;

        var connectionString =
            configuration.GetConnectionString("RabbitMQ") ?? "amqp://guest:guest@localhost:5672";

        _factory = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
        };

        _lazyConnection = new Lazy<Task<IConnection>>(async () =>
        {
            _logger.LogInformation(
                "Connecting to RabbitMQ asynchronously at {Uri}...",
                connectionString
            );
            var conn = await _factory.CreateConnectionAsync();
            _logger.LogInformation("RabbitMQ connection established.");
            await DeclareTopologyAsync(conn);
            return conn;
        });
    }

    public async Task<IChannel> CreateChannelAsync()
    {
        var connection = await _lazyConnection.Value;
        return await connection.CreateChannelAsync();
    }

    private async Task DeclareTopologyAsync(IConnection connection)
    {
        using var channel = await connection.CreateChannelAsync();

        // Dead letter exchange — declared first
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

        // ScyllaDB queue — fast path
        await channel.QueueDeclareAsync(
            queue: Topology.ScyllaMessageQueue,
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

        // Search index queue — slow path
        await channel.QueueDeclareAsync(
            queue: Topology.SearchIndexQueue,
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

        _logger.LogInformation("RabbitMQ topology declared.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_lazyConnection.IsValueCreated)
        {
            var connection = await _lazyConnection.Value;
            await connection.DisposeAsync();
        }
    }
}
