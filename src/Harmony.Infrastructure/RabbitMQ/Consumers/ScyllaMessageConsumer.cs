using System.Text;
using System.Text.Json;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Harmony.Infrastructure.RabbitMQ.Consumers;

/// <summary>
/// Background consumer that processes messages from the ScyllaDB write queue.
///
/// Broadcast rule: persist first via IMessageConsumerHandler, then broadcast
/// via IHubBroadcaster. Never broadcast before persistence is confirmed.
///
/// IHubBroadcaster is injected as a singleton — it holds the real
/// IHubContext{ChatHub, IChatClient} in the API layer. Infrastructure
/// never references ChatHub or any SignalR type directly.
/// </summary>
public class ScyllaMessageConsumer : BackgroundService
{
    private readonly RabbitMQConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubBroadcaster _hubBroadcaster;
    private readonly ILogger<ScyllaMessageConsumer> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;
    private IChannel? _channel;
    private string? _consumerTag;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ScyllaMessageConsumer(
        RabbitMQConnection connection,
        IServiceScopeFactory scopeFactory,
        IHubBroadcaster hubBroadcaster,
        ILogger<ScyllaMessageConsumer> logger
    )
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _hubBroadcaster = hubBroadcaster;
        _logger = logger;

        _retryPolicy = Policy
            .Handle<Exception>(ex => ex is not JsonException)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (exception, timeSpan, retryCount, _) =>
                    _logger.LogWarning(
                        exception,
                        "ScyllaConsumer: retry {RetryCount} after {Delay:0.0}s",
                        retryCount,
                        timeSpan.TotalSeconds
                    )
            );
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = await _connection.CreateChannelAsync();
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        _consumerTag = await _channel.BasicConsumeAsync(
            queue: Topology.ScyllaMessageQueue,
            autoAck: false,
            consumer: consumer
        );

        _logger.LogInformation(
            "ScyllaMessageConsumer started — listening on {Queue}",
            Topology.ScyllaMessageQueue
        );

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var routingKey = ea.RoutingKey;
        var body = Encoding.UTF8.GetString(ea.Body.Span);

        _logger.LogDebug("ScyllaConsumer received — RoutingKey: {RoutingKey}", routingKey);

        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<IMessageConsumerHandler>();

                switch (routingKey)
                {
                    case Topology.MessageSentKey:
                        await HandleMessageSentAsync(handler, body);
                        break;
                    case Topology.MessageDeletedKey:
                        await HandleMessageDeletedAsync(handler, body);
                        break;
                    case Topology.MessageEditedKey:
                        await HandleMessageEditedAsync(handler, body);
                        break;
                    default:
                        _logger.LogWarning(
                            "ScyllaConsumer — unrecognised routing key: {RoutingKey}",
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
                "ScyllaConsumer: unrecoverable failure for RoutingKey {RoutingKey} — routing to DLQ",
                routingKey
            );

            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    // -------------------------------------------------------------------------
    // Per-event handlers — persist first, broadcast second
    // -------------------------------------------------------------------------

    private async Task HandleMessageSentAsync(IMessageConsumerHandler handler, string body)
    {
        var evt = JsonSerializer.Deserialize<MessageSentEvent>(body, JsonOptions);
        if (evt is null)
            return;

        // 1. Persist to ScyllaDB + create mention notifications
        await handler.HandleMessageSentAsync(evt);

        // 2. Broadcast authoritative message to channel subscribers
        await _hubBroadcaster.BroadcastMessageReceivedAsync(
            new MessageResponse(
                MessageId: evt.MessageId,
                ChannelId: evt.ChannelId,
                GuildId: evt.GuildId,
                UserId: evt.UserId,
                Content: evt.Content,
                MessageType: evt.MessageType,
                IsDeleted: false,
                IsEdited: false,
                ReplyToId: evt.ReplyToId,
                MentionIds: evt.MentionIds,
                AttachmentIds: evt.AttachmentIds,
                SentAt: evt.SentAt.ToUnixTimeMilliseconds(),
                EditedAt: null
            )
        );

        _logger.LogInformation(
            "ScyllaConsumer: MessageSent persisted and broadcast — MessageId: {MessageId}, ChannelId: {ChannelId}",
            evt.MessageId,
            evt.ChannelId
        );
    }

    private async Task HandleMessageDeletedAsync(IMessageConsumerHandler handler, string body)
    {
        var evt = JsonSerializer.Deserialize<MessageDeletedEvent>(body, JsonOptions);
        if (evt is null)
            return;

        await handler.HandleMessageDeletedAsync(evt);

        await _hubBroadcaster.BroadcastMessageDeletedAsync(
            new MessageDeletedPayload(
                MessageId: evt.MessageId,
                ChannelId: evt.ChannelId,
                GuildId: evt.GuildId,
                DeletedByUserId: evt.DeletedByUserId,
                DeletedAt: evt.DeletedAt.ToUnixTimeMilliseconds()
            )
        );

        _logger.LogInformation(
            "ScyllaConsumer: MessageDeleted persisted and broadcast — MessageId: {MessageId}",
            evt.MessageId
        );
    }

    private async Task HandleMessageEditedAsync(IMessageConsumerHandler handler, string body)
    {
        var evt = JsonSerializer.Deserialize<MessageEditedEvent>(body, JsonOptions);
        if (evt is null)
            return;

        await handler.HandleMessageEditedAsync(evt);

        await _hubBroadcaster.BroadcastMessageEditedAsync(
            new MessageEditedPayload(
                MessageId: evt.MessageId,
                ChannelId: evt.ChannelId,
                GuildId: evt.GuildId,
                EditedByUserId: evt.EditedByUserId,
                NewContent: evt.NewContent,
                EditedAt: evt.EditedAt.ToUnixTimeMilliseconds()
            )
        );

        _logger.LogInformation(
            "ScyllaConsumer: MessageEdited persisted and broadcast — MessageId: {MessageId}",
            evt.MessageId
        );
    }

    // -------------------------------------------------------------------------
    // Graceful shutdown
    // -------------------------------------------------------------------------

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            try
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
            catch (ObjectDisposedException)
            {
                // Quietly ignore if RabbitMQ connection pool was already disposed by the host
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ScyllaConsumer: exception during shutdown — ignoring");
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
