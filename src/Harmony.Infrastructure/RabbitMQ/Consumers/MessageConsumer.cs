using System.Text;
using System.Text.Json;
using Harmony.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Harmony.Infrastructure.RabbitMQ.Consumers;

public class MessageConsumer : BackgroundService
{
    private readonly RabbitMQConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageConsumer> _logger;
    private IChannel? _channel;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public MessageConsumer(
        RabbitMQConnection connection,
        IServiceScopeFactory scopeFactory,
        ILogger<MessageConsumer> logger
    )
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = await _connection.CreateChannelAsync();

        // One message at a time — don't fetch next until current is acked
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: Topology.MessagePersistQueue,
            autoAck: false,
            consumer: consumer
        );

        _logger.LogInformation(
            "MessageConsumer started — listening on {Queue}",
            Topology.MessagePersistQueue
        );

        // Keep alive until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var routingKey = ea.RoutingKey;
        var body = Encoding.UTF8.GetString(ea.Body.Span);

        _logger.LogDebug("Received message with routing key: {RoutingKey}", routingKey);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IMessageConsumerHandler>();

            switch (routingKey)
            {
                case Topology.MessageSentKey:
                    var sentEvt = JsonSerializer.Deserialize<MessageSentEvent>(body, JsonOptions);
                    if (sentEvt is not null)
                        await handler.HandleMessageSentAsync(sentEvt);
                    break;

                case Topology.MessageDeletedKey:
                    var deletedEvt = JsonSerializer.Deserialize<MessageDeletedEvent>(
                        body,
                        JsonOptions
                    );
                    if (deletedEvt is not null)
                        await handler.HandleMessageDeletedAsync(deletedEvt);
                    break;

                case Topology.MessageEditedKey:
                    var editedEvt = JsonSerializer.Deserialize<MessageEditedEvent>(
                        body,
                        JsonOptions
                    );
                    if (editedEvt is not null)
                        await handler.HandleMessageEditedAsync(editedEvt);
                    break;

                default:
                    _logger.LogWarning(
                        "Unknown routing key: {RoutingKey} — nacking without requeue",
                        routingKey
                    );
                    await _channel!.BasicNackAsync(ea.DeliveryTag, false, false);
                    return;
            }

            // Ack only after successful processing
            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to process message with routing key: {RoutingKey} — nacking with requeue",
                routingKey
            );

            // Requeue on failure — RabbitMQ will redeliver
            // After x-message-ttl the message goes to dead letter exchange
            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
        }
    }

public override async Task StopAsync(CancellationToken cancellationToken)
{
    if (_channel is not null)
    {
        try
        {
            await _channel.CloseAsync();
        }
        catch (ObjectDisposedException) { /* already disposed, ignore */ }
        finally
        {
            await _channel.DisposeAsync();
        }
    }

    await base.StopAsync(cancellationToken);
}
}
