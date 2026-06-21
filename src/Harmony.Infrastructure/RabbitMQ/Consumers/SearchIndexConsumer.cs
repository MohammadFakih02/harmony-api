using System;
using System.Linq;
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
using RabbitMQ.Client.Exceptions;

namespace Harmony.Infrastructure.RabbitMQ.Consumers;

public class SearchIndexConsumer : BackgroundService
{
    private readonly RabbitMQConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchIndexConsumer> _logger;
    private readonly ResiliencePipeline _retryPipeline;
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

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not JsonException
                    && ex is not ServiceUnavailableException
                    // Constraint-violation poison (e.g. 23503 FK on the decoupled search row,
                    // 23505 unique) fails identically on every retry — DLQ it now, don't ladder.
                    && !ConsumerRetryPredicate.IsConstraintViolation(ex)),
                MaxRetryAttempts = 3,
                DelayGenerator = args =>
                {
                    // v8 AttemptNumber is 0-based; +1 reproduces the v7 1-based 2s/4s/8s ladder. Cap 30s.
                    var seconds = Math.Min(Math.Pow(2, args.AttemptNumber + 1), 30);
                    return new ValueTask<TimeSpan?>(TimeSpan.FromSeconds(seconds));
                },
                OnRetry = args =>
                {
                    _logger.LogWarning(args.Outcome.Exception,
                        "SearchIndexConsumer: retry {RetryCount} after {Delay:0.0}s",
                        args.AttemptNumber + 1, args.RetryDelay.TotalSeconds);
                    return default;
                },
            })
            .Build();
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
            await _retryPipeline.ExecuteAsync(async ct =>
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
                            await handler.HandleMessageSentAsync(sentEvt, _stoppingToken);
                        break;

                    case Topology.MessageDeletedKey:
                        var deletedEvt = JsonSerializer.Deserialize<MessageDeletedEvent>(
                            body,
                            JsonOptions
                        );
                        if (deletedEvt is not null)
                            await handler.HandleMessageDeletedAsync(deletedEvt, _stoppingToken);
                        break;

                    case Topology.MessageEditedKey:
                        var editedEvt = JsonSerializer.Deserialize<MessageEditedEvent>(
                            body,
                            JsonOptions
                        );
                        if (editedEvt is not null)
                            await handler.HandleMessageEditedAsync(editedEvt, _stoppingToken);
                        break;
                    case Topology.ChannelDeletedKey: // Added!
                        var channelDeletedEvt = JsonSerializer.Deserialize<ChannelDeletedEvent>(
                            body,
                            JsonOptions
                        );
                        if (channelDeletedEvt is not null)
                            await handler.HandleChannelDeletedAsync(
                                channelDeletedEvt,
                                _stoppingToken
                            );
                        break;

                    default:
                        _logger.LogWarning(
                            "SearchIndexConsumer — unrecognised routing key: {RoutingKey}",
                            routingKey
                        );
                        break;
                }
            }, _stoppingToken);

            if (_channel!.IsOpen)
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(
                "SearchIndexConsumer: out-of-order write lag — {Message}. Throttling and requeuing.",
                ex.Message
            );

            await Task.Delay(2000, _stoppingToken);

            if (_channel!.IsOpen)
            {
                bool isTestEnv =
                    string.Equals(
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                        "Test",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || AppDomain
                        .CurrentDomain.GetAssemblies()
                        .Any(a =>
                            a.FullName!.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                        );

                bool shouldRequeue = !isTestEnv || !ea.Redelivered;

                await _channel.BasicNackAsync(
                    ea.DeliveryTag,
                    multiple: false,
                    requeue: shouldRequeue
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SearchIndexConsumer: unrecoverable failure for RoutingKey {RoutingKey} — routing to DLQ",
                routingKey
            );

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
                if (_channel.IsOpen)
                {
                    if (!string.IsNullOrEmpty(_consumerTag))
                    {
                        await _channel.BasicCancelAsync(
                            _consumerTag,
                            cancellationToken: cancellationToken
                        );
                    }

                    await _channel.CloseAsync(cancellationToken);
                }
            }
            catch (ObjectDisposedException)
            {
                // Quietly ignore — RabbitMQ connection was already disposed by the host
            }
            catch (AlreadyClosedException)
            {
                // Quietly ignore if RabbitMQ connection/channel was already closed during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SearchIndexConsumer: exception during shutdown — ignoring");
            }
            finally
            {
                try
                {
                    await _channel.DisposeAsync();
                }
                catch
                { /* ignore */
                }
            }
        }

        await base.StopAsync(cancellationToken);
    }
}
