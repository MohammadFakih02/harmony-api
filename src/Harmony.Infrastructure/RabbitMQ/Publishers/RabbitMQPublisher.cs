using System.Text;
using System.Text.Json;
using Harmony.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Harmony.Infrastructure.RabbitMQ.Producers;

public class RabbitMQPublisher : IMessagePublisher, IAsyncDisposable
{
    private readonly RabbitMQConnection _connection;
    private readonly ILogger<RabbitMQPublisher> _logger;
    private IChannel? _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RabbitMQPublisher(RabbitMQConnection connection, ILogger<RabbitMQPublisher> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task PublishMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default)
    {
        await PublishAsync(Topology.MessageExchange, Topology.MessageSentKey, evt, ct);
        _logger.LogDebug(
            "Published MessageSent — MessageId: {MessageId}, ChannelId: {ChannelId}",
            evt.MessageId,
            evt.ChannelId
        );
    }

    public async Task PublishMessageDeletedAsync(
        MessageDeletedEvent evt,
        CancellationToken ct = default
    )
    {
        await PublishAsync(Topology.MessageExchange, Topology.MessageDeletedKey, evt, ct);
        _logger.LogDebug(
            "Published MessageDeleted — MessageId: {MessageId}, ChannelId: {ChannelId}",
            evt.MessageId,
            evt.ChannelId
        );
    }

    public async Task PublishMessageEditedAsync(
        MessageEditedEvent evt,
        CancellationToken ct = default
    )
    {
        await PublishAsync(Topology.MessageExchange, Topology.MessageEditedKey, evt, ct);
        _logger.LogDebug(
            "Published MessageEdited — MessageId: {MessageId}, ChannelId: {ChannelId}",
            evt.MessageId,
            evt.ChannelId
        );
    }

    private async Task<IChannel> GetChannelAsync()
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await _lock.WaitAsync();
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            if (_channel != null)
            {
                await _channel.DisposeAsync();
            }

            _channel = await _connection.CreateChannelAsync();
            return _channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PublishAsync<T>(
        string exchange,
        string routingKey,
        T message,
        CancellationToken ct
    )
    {
        var channel = await GetChannelAsync();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));

        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel != null)
        {
            await _channel.DisposeAsync();
        }
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
