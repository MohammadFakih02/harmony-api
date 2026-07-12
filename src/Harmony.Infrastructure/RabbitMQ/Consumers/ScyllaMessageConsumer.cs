using System.Text;
using System.Text.Json;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Exceptions;
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

    // Out-of-order edit handling: a MessageEdited whose MessageSent hasn't landed yet is
    // requeued with a short backoff, up to a bounded number of times before being DLQ'd — so a
    // permanently-failed Sent (already on the DLQ) can't make the edit requeue forever.
    private const int MaxOutOfOrderRequeues = 8;
    private static readonly TimeSpan OutOfOrderRequeueDelay = TimeSpan.FromSeconds(2);

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
            .AddRetry(
                new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                        ex is not JsonException
                        // Out-of-order edit signal — not retried on the ladder; handled by a
                        // dedicated catch that backs off and requeues (see OnMessageReceivedAsync).
                        && ex is not ServiceUnavailableException
                        // Symmetry + safety net: constraint-violation poison fast-fails to the DLQ
                        // instead of laddering. The Scylla write itself can't throw a PostgresException;
                        // the one Postgres touch (mention notifications) is best-effort in the handler,
                        // so in practice nothing constraint-shaped reaches this predicate here.
                        && !ConsumerRetryPredicate.IsConstraintViolation(ex)
                    ),
                    MaxRetryAttempts = 3,
                    DelayGenerator = args =>
                    {
                        // v8 AttemptNumber is 0-based; +1 reproduces the v7 1-based 2s/4s/8s ladder. Cap 30s.
                        var seconds = Math.Min(Math.Pow(2, args.AttemptNumber + 1), 30);
                        return new ValueTask<TimeSpan?>(TimeSpan.FromSeconds(seconds));
                    },
                    OnRetry = args =>
                    {
                        _logger.LogWarning(
                            args.Outcome.Exception,
                            "ScyllaConsumer: retry {RetryCount} after {Delay:0.0}s",
                            args.AttemptNumber + 1,
                            args.RetryDelay.TotalSeconds
                        );
                        return default;
                    },
                }
            )
            .Build();
    }

    // Self-healing reconnect backoff (the consume session ending isn't fatal — we re-subscribe).
    // Capped low (5s, not 30s): this is *connection recovery*, not the message-retry ladder, so a
    // tight cadence matters — a restarting broker answers ping in ~2s but refuses AMQP connections
    // for ~15-20s ("connection.start was never received"), and we want to re-subscribe within a few
    // seconds of it becoming ready rather than sitting in a long backoff.
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        // A BackgroundService that subscribes once and waits forever goes permanently silent the
        // moment its channel/connection drops (RabbitMQ restart, transient network blip). Instead we
        // run a self-healing loop: subscribe, wait until the channel signals shutdown, then dispose
        // it and re-subscribe on a fresh one. Recovery no longer depends on the library quietly
        // re-attaching the consumer (which it does not do reliably here).
        var delay = InitialReconnectDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConsumeSessionAsync(stoppingToken);
                // A clean return means the channel/connection dropped (not cancellation) — reconnect
                // promptly with a fresh backoff window.
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
                    "ScyllaMessageConsumer: consume session failed — reconnecting in {Delay:0.0}s",
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

        _logger.LogInformation("ScyllaMessageConsumer stopped.");
    }

    /// <summary>
    /// Opens a fresh channel, subscribes, and blocks until the channel/connection shuts down or the
    /// host stops. Returns normally on a channel drop so the outer loop reconnects; the channel is
    /// always disposed here (so the library can't recover it to race the next subscription).
    /// </summary>
    private async Task RunConsumeSessionAsync(CancellationToken stoppingToken)
    {
        var connection = await _connection.GetConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        _channel = channel;

        // prefetchCount > 1 buffers deliveries client-side to remove the per-message ack
        // round-trip stall. Ordering is preserved because consumer dispatch concurrency stays
        // at the default of 1 (set on the connection, not here) — a single dispatch thread
        // processes deliveries serially. Raising dispatch concurrency would reintroduce
        // reordering; the out-of-order edit requeue path below is the safety net if it ever is.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false);

        // Completes when the channel reports shutdown / a callback exception, or the host stops —
        // replaces the old `Task.Delay(Timeout.Infinite)` so a dead channel actually wakes the loop.
        var sessionEnded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Task OnChannelShutdown(object? _, ShutdownEventArgs e)
        {
            _logger.LogWarning(
                "ScyllaMessageConsumer: channel shut down ({Reason}) — will re-subscribe",
                e.ReplyText
            );
            sessionEnded.TrySetResult();
            return Task.CompletedTask;
        }

        Task OnCallbackException(object? _, CallbackExceptionEventArgs e)
        {
            _logger.LogWarning(e.Exception, "ScyllaMessageConsumer: channel callback exception");
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
                queue: Topology.ScyllaMessageQueue,
                autoAck: false,
                consumer: consumer
            );

            _logger.LogInformation(
                "ScyllaMessageConsumer started — listening on {Queue}",
                Topology.ScyllaMessageQueue
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

        _logger.LogDebug("ScyllaConsumer received — RoutingKey: {RoutingKey}", routingKey);

        try
        {
            await _retryPipeline.ExecuteAsync(
                async ct =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var handler =
                        scope.ServiceProvider.GetRequiredService<IMessageConsumerHandler>();

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
                },
                _stoppingToken
            );

            await deliveryChannel.BasicAckAsync(ea.DeliveryTag, multiple: false);
        }
        catch (ServiceUnavailableException ex)
        {
            // Out-of-order edit: its MessageSent hasn't been persisted yet. Back off and requeue
            // so the insert lands first — mirrors SearchIndexConsumer. Bounded by a Redis attempt
            // counter so a permanently-failed Sent doesn't loop the edit forever.
            _logger.LogWarning(
                "ScyllaConsumer: out-of-order edit — {Message}. Throttling and requeuing.",
                ex.Message
            );

            // The dedup key was claimed on this first delivery (in HandleMessageEditedAsync, before
            // the handler threw). Clear it so the requeued delivery re-processes instead of being
            // swallowed as a duplicate (Decision D). The handler throws before the broadcast, so
            // there is no double-broadcast/double-unread risk from relaxing dedup here.
            long messageId = 0;
            try
            {
                var evt = JsonSerializer.Deserialize<MessageEditedEvent>(body, JsonOptions);
                if (evt is not null)
                {
                    messageId = evt.MessageId;
                    await _deduplicator.ClearAsync(IMessageDeduplicator.Edited, evt.MessageId);
                }
            }
            catch (Exception clearEx)
            {
                _logger.LogWarning(
                    clearEx,
                    "ScyllaConsumer: failed to clear dedup key on out-of-order requeue — continuing"
                );
            }

            // Backoff so we don't hot-loop at low queue depth (the delay runs on the single
            // dispatch thread, so it also throttles the queue — acceptable for this rare path).
            await Task.Delay(OutOfOrderRequeueDelay, _stoppingToken);

            if (deliveryChannel.IsOpen)
            {
                var attempts = await _deduplicator.IncrementRequeueCountAsync(
                    IMessageDeduplicator.Edited,
                    messageId
                );

                // In the Test env, requeue at most once (then DLQ) so a stuck edit can't burn the
                // full backoff budget inside a test run — mirrors SearchIndexConsumer. (Reuses the
                // xunit env-detection that §18 flags for cleanup; kept for consistency.)
                var shouldRequeue =
                    attempts < MaxOutOfOrderRequeues && (!IsTestEnv() || !ea.Redelivered);

                if (!shouldRequeue)
                    _logger.LogError(
                        "ScyllaConsumer: out-of-order edit exceeded requeue budget ({Attempts}) — routing to DLQ",
                        attempts
                    );

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

            await deliveryChannel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
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
            _logger.LogWarning(
                ex,
                "ScyllaConsumer: could not fetch sender info for {UserId}",
                evt.UserId
            );
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
                EditedAt: null,
                // A brand-new message has no reactions yet — they arrive via ReactionAdded events.
                Reactions: []
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

    // Detects the integration-test environment so the out-of-order requeue can DLQ after a
    // single redelivery rather than burning the full backoff budget. Mirrors SearchIndexConsumer.
    private static bool IsTestEnv() =>
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Test",
            StringComparison.OrdinalIgnoreCase
        )
        || AppDomain
            .CurrentDomain.GetAssemblies()
            .Any(a => a.FullName!.Contains("xunit", StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------------
    // Graceful shutdown
    // -------------------------------------------------------------------------

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The consume loop owns the channel lifecycle: cancelling the stopping token completes the
        // session's TCS, whose finally cancels + closes + disposes the channel. So we just signal
        // cancellation and await the loop — closing the channel here too would race that teardown.
        await base.StopAsync(cancellationToken);
    }
}
