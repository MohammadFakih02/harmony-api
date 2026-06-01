using System.Text;
using System.Text.Json;
using Harmony.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Harmony.Infrastructure.RabbitMQ.Consumers;

public class SearchIndexConsumer : BackgroundService
{
    private readonly RabbitMQConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchIndexConsumer> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;
    private IChannel? _channel;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SearchIndexConsumer(
        RabbitMQConnection connection,
        IServiceScopeFactory scopeFactory,
        ILogger<SearchIndexConsumer> logger
    )
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _retryPolicy = Policy
            .Handle<Exception>(ex => !(ex is JsonException))
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        exception,
                        "Error processing Postgres search index message. Retry {RetryCount} after {Delay}s",
                        retryCount,
                        timeSpan.TotalSeconds
                    );
                }
            );
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = await _connection.CreateChannelAsync();

        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: Topology.SearchIndexQueue,
            autoAck: false,
            consumer: consumer
        );

        _logger.LogInformation(
            "SearchIndexConsumer started — listening on {Queue}",
            Topology.SearchIndexQueue
        );

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var routingKey = ea.RoutingKey;
        var body = Encoding.UTF8.GetString(ea.Body.Span);

        _logger.LogDebug(
            "SearchIndexConsumer received message with routing key: {RoutingKey}",
            routingKey
        );

        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var handler =
                    scope.ServiceProvider.GetRequiredService<SearchIndexConsumerHandler>();

                switch (routingKey)
                {
                    case Topology.MessageSentKey:
                        var sentEvt = JsonSerializer.Deserialize<MessageSentEvent>(
                            body,
                            JsonOptions
                        );
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
                            "SearchIndexConsumer — unknown routing key: {RoutingKey}",
                            routingKey
                        );
                        break;
                }
            });

            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SearchIndexConsumer failed to process message with routing key: {RoutingKey} after retries. Routing to DLQ.",
                routingKey
            );

            // Requeue = false routes it to the Dead Letter Exchange
            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
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
            catch (ObjectDisposedException) { }
            finally
            {
                await _channel.DisposeAsync();
            }
        }

        await base.StopAsync(cancellationToken);
    }
}
