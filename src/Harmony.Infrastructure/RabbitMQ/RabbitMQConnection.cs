using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Harmony.Infrastructure.RabbitMQ;

public class RabbitMQConnection : IAsyncDisposable
{
    private readonly ILogger<RabbitMQConnection> _logger;
    private readonly ConnectionFactory _factory;
    private readonly string _connectionString;

    // Guards (re)connection so concurrent callers can't open duplicate connections. We deliberately
    // do NOT use Lazy<Task<IConnection>>: a Lazy caches the faulted task forever if the very first
    // connect throws (broker not up at boot), permanently poisoning the process. This pattern caches
    // a connection only on success and re-attempts on the next call.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    public RabbitMQConnection(IConfiguration configuration, ILogger<RabbitMQConnection> logger)
    {
        _logger = logger;

        _connectionString =
            configuration.GetConnectionString("RabbitMQ") ?? "amqp://guest:guest@localhost:5672";

        _factory = new ConnectionFactory
        {
            Uri = new Uri(_connectionString),
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>
    /// Returns a live connection, establishing (or re-establishing) one on demand. Unlike a
    /// <c>Lazy&lt;Task&gt;</c>, a failed attempt is never cached — the next call retries — so a broker
    /// that is briefly unavailable at startup or between restarts can never poison the process.
    /// Consumers also use the returned connection to subscribe to its shutdown events.
    /// </summary>
    public async Task<IConnection> GetConnectionAsync()
    {
        var existing = _connection;
        if (existing is { IsOpen: true })
            return existing;

        await _gate.WaitAsync();
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            // Drop a dead/closed connection before reconnecting.
            if (_connection is not null)
            {
                try
                {
                    await _connection.DisposeAsync();
                }
                catch
                { /* ignore */
                }
                _connection = null;
            }

            _logger.LogInformation("Connecting to RabbitMQ at {Uri}...", _connectionString);
            var conn = await _factory.CreateConnectionAsync();
            await DeclareTopologyAsync(conn);
            _connection = conn;
            _logger.LogInformation("RabbitMQ connection established.");
            return conn;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IChannel> CreateChannelAsync()
    {
        var connection = await GetConnectionAsync();
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

        // BIND ScyllaMessageQueue to Channel Deletion Event [12]
        await channel.QueueBindAsync(
            queue: Topology.ScyllaMessageQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.ChannelDeletedKey
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

        // BIND SearchIndexQueue to Channel Deletion Event [12]
        await channel.QueueBindAsync(
            queue: Topology.SearchIndexQueue,
            exchange: Topology.MessageExchange,
            routingKey: Topology.ChannelDeletedKey
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
        if (_disposed)
            return;
        _disposed = true;

        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch
            { /* ignore */
            }
            _connection = null;
        }

        _gate.Dispose();
    }
}
