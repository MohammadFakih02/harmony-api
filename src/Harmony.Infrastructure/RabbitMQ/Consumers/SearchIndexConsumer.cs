using System.Text;
using System.Text.Json;
using Harmony.Application.Exceptions;
using Harmony.Domain.Interfaces;
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
    private string? _consumerTag;
    private CancellationToken _stoppingToken;

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
            .Handle<Exception>(ex => ex is not JsonException && ex is not ServiceUnavailableException)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (exception, timeSpan, retryCount, _) =>
                    _logger.LogWarning(exception,
                        "SearchIndexConsumer: retry {RetryCount} after {Delay:0.0}s",
                        retryCount, timeSpan.TotalSeconds)
            );
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _channel = await _connection.CreateChannelAsync();
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        _consumerTag = await _channel.BasicConsumeAsync(
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

        _logger.LogDebug("SearchIndexConsumer received — RoutingKey: {RoutingKey}", routingKey);

        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<SearchIndexConsumerHandler>();

                switch (routingKey)
                {
                    case Topology.MessageSentKey:
                        var sentEvt = JsonSerializer.Deserialize<MessageSentEvent>(body, JsonOptions);
                        if (sentEvt is not null)
                            await handler.HandleMessageSentAsync(sentEvt, _stoppingToken);
                        break;

                    case Topology.MessageDeletedKey:
                        var deletedEvt = JsonSerializer.Deserialize<MessageDeletedEvent>(body, JsonOptions);
                        if (deletedEvt is not null)
                            await handler.HandleMessageDeletedAsync(deletedEvt, _stoppingToken);
                        break;

                    case Topology.MessageEditedKey:
                        var editedEvt = JsonSerializer.Deserialize<MessageEditedEvent>(body, JsonOptions);
                        if (editedEvt is not null)
                            await handler.HandleMessageEditedAsync(editedEvt, _stoppingToken);
                        break;

                    default:
                        _logger.LogWarning(
                            "SearchIndexConsumer — unrecognised routing key: {RoutingKey}", routingKey);
                        break;
                }
            });

            // Guard: only ack if channel is still open (host may be shutting down)
            if (_channel!.IsOpen)
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(
                "SearchIndexConsumer: out-of-order write lag — {Message}. Throttling and requeuing.",
                ex.Message);

            await Task.Delay(2000, _stoppingToken);

            if (_channel!.IsOpen)
                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SearchIndexConsumer: unrecoverable failure for RoutingKey {RoutingKey} — routing to DLQ",
                routingKey);

            if (_channel!.IsOpen)
                await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            try
            {
                if (!string.IsNullOrEmpty(_consumerTag) && _channel.IsOpen)
                    await _channel.BasicCancelAsync(_consumerTag, cancellationToken: cancellationToken);

                if (_channel.IsOpen)
                    await _channel.CloseAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SearchIndexConsumer: exception during shutdown — ignoring");
            }
            finally
            {
                try { await _channel.DisposeAsync(); } catch { /* ignore */ }
            }
        }

        await base.StopAsync(cancellationToken);
    }
}
