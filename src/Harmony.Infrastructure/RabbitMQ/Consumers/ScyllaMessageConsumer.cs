using System.Text;
using System.Text.Json;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Harmony.Infrastructure.RabbitMQ.Consumers;

/// <summary>
/// Background consumer that processes messages from the ScyllaDB write queue.
///
/// Processing order per message:
///   1. Deduplication gate  — Redis SET NX (skip immediately if duplicate, still ack)
///   2. Handler             — persist to ScyllaDB via IMessageConsumerHandler
///   3. Broadcast           — push to SignalR clients via IHubBroadcaster
///   4. Ack                 — BasicAck to RabbitMQ
///
/// On unrecoverable failure after retries: BasicNack requeue=false → DLQ.
/// On duplicate detected: return early, outer try still BasicAcks — the message
/// was already processed successfully on a prior delivery.
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
    private readonly IMessageDeduplicator _deduplicator;
    private readonly ILogger<ScyllaMessageConsumer> _logger;
    private readonly ResiliencePipeline _retryPipeline;
    private IChannel? _channel;
    private string? _consumerTag;
    private CancellationToken _stoppingToken;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ScyllaMessageConsumer(
        RabbitMQConnection connection,
        IServiceScopeFactory scopeFactory,
        IHubBroadcaster hubBroadcaster,
        IMessageDeduplicator deduplicator,
        ILogger<ScyllaMessageConsumer> logger
    )
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _hubBroadcaster = hubBroadcaster;
        _deduplicator = deduplicator;
        _logger = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not JsonException
                    // Symmetry + safety net: constraint-violation poison fast-fails to the DLQ
                    // instead of laddering. The Scylla write itself can't throw a PostgresException;
                    // the one Postgres touch (mention notifications) is best-effort in the handler,
                    // so in practice nothing constraint-shaped reaches this predicate here.
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
                        "ScyllaConsumer: retry {RetryCount} after {Delay:0.0}s",
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
            await _retryPipeline.ExecuteAsync(async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<IMessageConsumerHandler>();

                switch (routingKey)
                {
                    case Topology.MessageSentKey:
                        await HandleMessageSentAsync(scope.ServiceProvider, handler, body);
                        break;
                    case Topology.MessageDeletedKey:
                        await HandleMessageDeletedAsync(handler, body);
                        break;
                    case Topology.MessageEditedKey:
                        await HandleMessageEditedAsync(handler, body);
                        break;
                    case Topology.ChannelDeletedKey:
                        await HandleChannelDeletedAsync(handler, body);
                        break;
                    default:
                        _logger.LogWarning(
                            "ScyllaConsumer — unrecognised routing key: {RoutingKey}",
                            routingKey
                        );
                        break;
                }
            }, _stoppingToken);

            await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ScyllaConsumer: unrecoverable failure for RoutingKey {RoutingKey} — routing to DLQ",
                routingKey
            );

            if (routingKey == Topology.MessageSentKey)
            {
                // Best-effort: notify the sender and clear the dedup key.
                // If parsing fails (JsonException → null), skip silently — message still goes to DLQ.
                // Wrapped in its own try/catch so a notification failure never suppresses the BasicNack.
                try
                {
                    var evt = JsonSerializer.Deserialize<MessageSentEvent>(body, JsonOptions);
                    if (evt is not null)
                    {
                        await _hubBroadcaster.BroadcastMessageFailedAsync(
                            evt.UserId,
                            new MessageFailedPayload(evt.MessageId, evt.ChannelId, evt.GuildId)
                        );

                        // Decision D: clear the dedup key so a genuine RabbitMQ redelivery can
                        // recover. Scylla writes are idempotent upserts — safe to reprocess.
                        await _deduplicator.ClearAsync(IMessageDeduplicator.Sent, evt.MessageId);
                    }
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(
                        notifyEx,
                        "ScyllaConsumer: failed to notify sender of message failure — nacking anyway"
                    );
                }
            }

            await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    // -------------------------------------------------------------------------
    // Per-event handlers — dedup → persist → broadcast
    // -------------------------------------------------------------------------

    private async Task HandleMessageSentAsync(
        IServiceProvider services,
        IMessageConsumerHandler handler,
        string body
    )
    {
        var evt = JsonSerializer.Deserialize<MessageSentEvent>(body, JsonOptions);
        if (evt is null)
            return;

        // 1. Deduplication gate — skip if already processed (protects the increment too)
        if (await _deduplicator.IsDuplicateAsync(IMessageDeduplicator.Sent, evt.MessageId))
        {
            _logger.LogInformation(
                "ScyllaConsumer: duplicate MessageSent skipped — MessageId: {MessageId}",
                evt.MessageId
            );
            return;
        }

        // 2. Persist to ScyllaDB + create mention notifications
        await handler.HandleMessageSentAsync(evt);

        // 3. Broadcast authoritative message to channel subscribers
        // Fetch sender's display info for the client (best-effort; falls back to "Unknown")
        string senderUsername = "Unknown";
        string? senderAvatarKey = null;
        try
        {
            var userRepo = services.GetRequiredService<IUserRepository>();
            var sender = await userRepo.GetByIdAsync(evt.UserId);
            senderUsername = sender?.UserName ?? "Unknown";
            senderAvatarKey = sender?.AvatarKey;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScyllaConsumer: could not fetch sender info for {UserId}", evt.UserId);
        }

        await _hubBroadcaster.BroadcastMessageReceivedAsync(
            new MessageResponse(
                MessageId: evt.MessageId,
                ChannelId: evt.ChannelId,
                GuildId: evt.GuildId,
                UserId: evt.UserId,
                Username: senderUsername,
                AvatarKey: senderAvatarKey,
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

        // 4. Unread fan-out — best-effort. MUST be swallowed: the message is already
        //    persisted and broadcast, so an unread failure must not bubble into the
        //    retry policy (which would re-broadcast). Resolved per-scope (scoped service).
        try
        {
            var unread = services.GetRequiredService<IUnreadCountService>();
            await unread.IncrementForChannelAsync(evt.GuildId, evt.ChannelId, evt.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ScyllaConsumer: unread fan-out failed for MessageId {MessageId} — message already persisted/broadcast, continuing",
                evt.MessageId
            );
        }

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

        // 1. Deduplication gate
        if (await _deduplicator.IsDuplicateAsync(IMessageDeduplicator.Deleted, evt.MessageId))
        {
            _logger.LogInformation(
                "ScyllaConsumer: duplicate MessageDeleted skipped — MessageId: {MessageId}",
                evt.MessageId
            );
            return;
        }

        // 2. Soft-delete in ScyllaDB
        await handler.HandleMessageDeletedAsync(evt);

        // 3. Notify channel subscribers
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

        // 1. Deduplication gate
        if (await _deduplicator.IsDuplicateAsync(IMessageDeduplicator.Edited, evt.MessageId))
        {
            _logger.LogInformation(
                "ScyllaConsumer: duplicate MessageEdited skipped — MessageId: {MessageId}",
                evt.MessageId
            );
            return;
        }

        // 2. Update content in ScyllaDB
        await handler.HandleMessageEditedAsync(evt);

        // 3. Notify channel subscribers
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

    private async Task HandleChannelDeletedAsync(IMessageConsumerHandler handler, string body)
    {
        var evt = JsonSerializer.Deserialize<ChannelDeletedEvent>(body, JsonOptions);
        if (evt is null)
            return;

        // No deduplication for ChannelDeleted — partition purges are idempotent
        // in ScyllaDB (deleting an already-empty partition is a no-op), so the
        // Redis round-trip overhead is not worth it here.
        await handler.HandleChannelDeletedAsync(evt);

        _logger.LogInformation(
            "ScyllaConsumer: ChannelDeleted handled and purged — ChannelId: {ChannelId}",
            evt.ChannelId
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
                // Quietly ignore if RabbitMQ connection pool was already disposed by the host
            }
            catch (AlreadyClosedException)
            {
                // Quietly ignore if RabbitMQ channel/connection was already closed during shutdown
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
