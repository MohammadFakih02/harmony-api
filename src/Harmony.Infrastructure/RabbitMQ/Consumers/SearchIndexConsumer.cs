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
    private readonly bool _isTestEnv;
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
        IHostEnvironment hostEnvironment,
        ILogger<SearchIndexConsumer> logger
    )
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _isTestEnv = hostEnvironment.IsEnvironment("Test");
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

    // Self-healing reconnect backoff — capped low (5s) for fast re-subscribe after a broker restart.
    // See ScyllaMessageConsumer for the rationale (this is connection recovery, not the retry ladder).
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        // Self-healing loop: subscribe, wait until the channel signals shutdown, then dispose it and
        // re-subscribe on a fresh channel — so a RabbitMQ restart no longer silences this consumer
        // until the backend is restarted. Mirrors ScyllaMessageConsumer.
        var delay = InitialReconnectDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConsumeSessionAsync(stoppingToken);
                delay = InitialReconnectDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "SearchIndexConsumer: consume session failed — reconnecting in {Delay:0.0}s",
                    delay.TotalSeconds
                );
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(
                Math.Min(delay.TotalSeconds * 2, MaxReconnectDelay.TotalSeconds)
            );
        }

        _logger.LogInformation("SearchIndexConsumer stopped.");
    }

    /// <summary>
    /// Opens a fresh channel, subscribes, and blocks until the channel/connection shuts down or the
    /// host stops. Returns normally on a channel drop so the outer loop reconnects; the channel is
    /// always disposed here so the library can't recover it to race the next subscription.
    /// </summary>
    private async Task RunConsumeSessionAsync(CancellationToken stoppingToken)
    {
        var connection = await _connection.GetConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        _channel = channel;

        // Higher prefetch than the Scylla consumer: FTS writes are idempotent and the consumer
        // already tolerates reordering via its ServiceUnavailableException requeue, so it benefits
        // most from buffering. Ordering still holds — dispatch concurrency stays at the default 1.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 20, global: false);

        var sessionEnded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Task OnChannelShutdown(object? _, ShutdownEventArgs e)
        {
            _logger.LogWarning(
                "SearchIndexConsumer: channel shut down ({Reason}) — will re-subscribe",
                e.ReplyText
            );
            sessionEnded.TrySetResult();
            return Task.CompletedTask;
        }

        Task OnCallbackException(object? _, CallbackExceptionEventArgs e)
        {
            _logger.LogWarning(e.Exception, "SearchIndexConsumer: channel callback exception");
            sessionEnded.TrySetResult();
            return Task.CompletedTask;
        }

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;
        channel.ChannelShutdownAsync += OnChannelShutdown;
        channel.CallbackExceptionAsync += OnCallbackException;

        try
        {
            _consumerTag = await channel.BasicConsumeAsync(
                queue: Topology.SearchIndexQueue,
                autoAck: false,
                consumer: consumer
            );

            _logger.LogInformation(
                "SearchIndexConsumer started — listening on {Queue}",
                Topology.SearchIndexQueue
            );

            await using (stoppingToken.Register(() => sessionEnded.TrySetResult()))
            {
                await sessionEnded.Task;
            }
        }
        finally
        {
            consumer.ReceivedAsync -= OnMessageReceivedAsync;
            channel.ChannelShutdownAsync -= OnChannelShutdown;
            channel.CallbackExceptionAsync -= OnCallbackException;
            await TearDownChannelAsync(channel);
            _channel = null;
        }
    }

    /// <summary>Gracefully cancels + closes + disposes a channel, ignoring shutdown-time races.</summary>
    private async Task TearDownChannelAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen && !string.IsNullOrEmpty(_consumerTag))
                await channel.BasicCancelAsync(_consumerTag, noWait: false);
        }
        catch
        { /* the channel may already be gone */
        }

        try
        {
            if (channel.IsOpen)
                await channel.CloseAsync(CancellationToken.None);
        }
        catch
        { /* ignore */
        }
        finally
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch
            { /* ignore */
            }
        }
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var routingKey = ea.RoutingKey;
        var body = Encoding.UTF8.GetString(ea.Body.Span);

        // Ack/Nack the channel this delivery actually arrived on — NOT the shared _channel field,
        // which the self-healing loop may swap mid-flight (acking a disposed channel would throw).
        var deliveryChannel = ((AsyncEventingBasicConsumer)sender).Channel;

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

            if (deliveryChannel.IsOpen)
                await deliveryChannel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(
                "SearchIndexConsumer: out-of-order write lag — {Message}. Throttling and requeuing.",
                ex.Message
            );

            await Task.Delay(2000, _stoppingToken);

            if (deliveryChannel.IsOpen)
            {
                // In the Test env (resolved once from the injected IHostEnvironment), requeue at most
                // once — then DLQ — so a stuck out-of-order write can't burn the full backoff budget.
                bool shouldRequeue = !_isTestEnv || !ea.Redelivered;

                await deliveryChannel.BasicNackAsync(
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

            if (deliveryChannel.IsOpen)
                await deliveryChannel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The consume loop owns the channel lifecycle (its session finally cancels + closes +
        // disposes on cancellation), so we just signal cancellation and await the loop.
        await base.StopAsync(cancellationToken);
    }
}
