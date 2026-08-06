using System.Text;
using System.Text.Json;
using Cassandra;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Exceptions;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Scylla;
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
    private readonly IScyllaSessionFactory _sessionFactory;
    private readonly ILogger<ScyllaMessageConsumer> _logger;
    private readonly ResiliencePipeline _retryPipeline;
    private readonly bool _isTestEnv;
    private IChannel? _channel;
    private string? _consumerTag;
    private CancellationToken _stoppingToken;

    // ── Scylla write-side circuit breaker ────────────────────────────────────────────────────
    // When Scylla is unreachable, retrying per-message (on the single dispatch thread) both burns
    // the ladder pointlessly and head-of-line-blocks the queue. Instead we treat a
    // NoHostAvailableException as "dependency down": requeue the in-flight message (durable), end
    // the consume session, and pause — probing Scylla every ScyllaProbeInterval — until it answers,
    // then re-subscribe. Messages wait safely in the queue and drain automatically on recovery.
    // _scyllaCircuitOpen is written on the dispatch thread and read on the consume-loop thread.
    private volatile bool _scyllaCircuitOpen;
    // The current session's completion source, so the dispatch handler can end the session (mirrors
    // the channel-shutdown callbacks). Reassigned per session; TrySetResult is idempotent.
    private volatile TaskCompletionSource? _sessionEnded;
    private static readonly TimeSpan ScyllaProbeInterval = TimeSpan.FromSeconds(3);
    private const int ScyllaProbeReadTimeoutMs = 3000;

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
        IScyllaSessionFactory sessionFactory,
        IHostEnvironment hostEnvironment,
        ILogger<ScyllaMessageConsumer> logger
    )
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _hubBroadcaster = hubBroadcaster;
        _deduplicator = deduplicator;
        _sessionFactory = sessionFactory;
        _isTestEnv = hostEnvironment.IsEnvironment("Test");
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
                        // Scylla unreachable — not retried on the ladder either. A dedicated catch
                        // trips the write-side circuit breaker: requeue + pause + probe until healthy,
                        // instead of burning the ladder + head-of-line-blocking the queue.
                        && ex is not NoHostAvailableException
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
            // Write-side circuit breaker: if a delivery hit Scylla-unreachable, don't re-subscribe
            // (and start re-failing + requeuing) until Scylla actually answers. Messages wait in the
            // durable queue meanwhile. Health-gate here rather than spinning the ladder per message.
            if (_scyllaCircuitOpen)
            {
                await WaitForScyllaHealthyAsync(stoppingToken);
                _scyllaCircuitOpen = false;
                if (stoppingToken.IsCancellationRequested)
                    break;
                delay = InitialReconnectDelay;
            }

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

        // Completes when the channel reports shutdown / a callback exception, the host stops, or a
        // delivery trips the Scylla circuit breaker — replaces the old `Task.Delay(Timeout.Infinite)`
        // so a dead channel (or an unreachable Scylla) actually wakes the loop.
        var sessionEnded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // Expose it so the dispatch handler can end this session on Scylla-unreachable.
        _sessionEnded = sessionEnded;

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

        // ── Deduplication pre-check — ONCE per delivery, OUTSIDE the retry ladder ─────────────
        // The dedup gate is check-and-set (Redis SET NX). It must NOT live inside the retried
        // lambda: attempt 0 claims the key, so every subsequent retry attempt re-hits its own key,
        // reads it as a "duplicate", returns normally, and the delivery gets ACKed — even though
        // the persist never succeeded. That silently drops the message (never persisted, never
        // DLQ'd, no MessageFailed broadcast → the sender's optimistic bubble stays grey forever).
        // Evaluating it here, exactly once, lets the pipeline actually retry the persist and — on
        // terminal failure — reach the DLQ + MessageFailed path in the catch below.
        var dedupKey = ResolveDedupKey(routingKey, body);
        if (dedupKey is { } dk && await _deduplicator.IsDuplicateAsync(dk.EventType, dk.MessageId))
        {
            _logger.LogInformation(
                "ScyllaConsumer: duplicate {EventType} skipped — MessageId: {MessageId}",
                dk.EventType,
                dk.MessageId
            );
            if (deliveryChannel.IsOpen)
                await deliveryChannel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            return;
        }

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
            // Same per-edit discriminator as the dedup gate, so clear/requeue-count target the right key.
            string editedEventType = IMessageDeduplicator.Edited;
            try
            {
                var evt = JsonSerializer.Deserialize<MessageEditedEvent>(body, JsonOptions);
                if (evt is not null)
                {
                    messageId = evt.MessageId;
                    editedEventType = EditedEventType(evt);
                    await _deduplicator.ClearAsync(editedEventType, evt.MessageId);
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
                    editedEventType,
                    messageId
                );

                // In the Test env, requeue at most once (then DLQ) so a stuck edit can't burn the
                // full backoff budget inside a test run — mirrors SearchIndexConsumer.
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
        catch (NoHostAvailableException ex)
        {
            // Scylla is unreachable — this is a DEPENDENCY outage, not a poison message. Do NOT DLQ
            // (that would fail every message of a transient outage) and do NOT ladder (it just
            // head-of-line-blocks the single dispatch thread). Instead: clear the dedup claim,
            // requeue the message durably, and trip the circuit breaker so the consume loop pauses
            // and probes Scylla until it recovers — then this message redelivers and persists.
            _logger.LogWarning(
                ex,
                "ScyllaConsumer: Scylla unreachable for {RoutingKey} — requeuing and pausing until healthy",
                routingKey
            );

            // CRITICAL: the dedup key was claimed in the pre-check above. Clear it, or the requeued
            // redelivery is swallowed as a duplicate and lost. Scylla writes are idempotent upserts,
            // so reprocessing is safe. Best-effort — a clear failure must not suppress the requeue.
            try
            {
                var requeueKey = ResolveDedupKey(routingKey, body);
                if (requeueKey is { } key)
                    await _deduplicator.ClearAsync(key.EventType, key.MessageId);
            }
            catch (Exception clearEx)
            {
                _logger.LogWarning(
                    clearEx,
                    "ScyllaConsumer: failed to clear dedup key before circuit-breaker requeue — continuing"
                );
            }

            if (deliveryChannel.IsOpen)
                await deliveryChannel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);

            // Open the breaker and end the session; the outer loop health-gates before re-subscribing.
            // Other prefetched-but-undispatched deliveries are requeued by the channel teardown.
            _scyllaCircuitOpen = true;
            _sessionEnded?.TrySetResult();
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

    /// <summary>
    /// Resolves the (eventType, messageId) dedup key for a delivery — evaluated ONCE, before the
    /// retry ladder (see <see cref="OnMessageReceivedAsync"/>). Returns <c>null</c> for routing
    /// keys that aren't deduplicated (ChannelDeleted / unknown) or bodies that don't parse — in the
    /// unparseable case the retry pipeline re-throws the same JsonException and the delivery DLQs,
    /// exactly as before. Never throws.
    /// </summary>
    private (string EventType, long MessageId)? ResolveDedupKey(string routingKey, string body)
    {
        try
        {
            switch (routingKey)
            {
                case Topology.MessageSentKey:
                    var sent = JsonSerializer.Deserialize<MessageSentEvent>(body, JsonOptions);
                    return sent is null ? null : (IMessageDeduplicator.Sent, sent.MessageId);
                case Topology.MessageDeletedKey:
                    var deleted = JsonSerializer.Deserialize<MessageDeletedEvent>(body, JsonOptions);
                    return deleted is null ? null : (IMessageDeduplicator.Deleted, deleted.MessageId);
                case Topology.MessageEditedKey:
                    var edited = JsonSerializer.Deserialize<MessageEditedEvent>(body, JsonOptions);
                    return edited is null ? null : (EditedEventType(edited), edited.MessageId);
                default:
                    // ChannelDeleted (idempotent partition purge, never deduped) + unknown keys.
                    return null;
            }
        }
        catch (JsonException)
        {
            // Let the retry pipeline hit the same JsonException (its ShouldHandle excludes
            // JsonException → immediate throw → DLQ). Swallowing it here would change that.
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Scylla circuit breaker — pause + probe while the store is unreachable
    // -------------------------------------------------------------------------

    /// <summary>
    /// Blocks until a lightweight Scylla query succeeds (or the host stops), probing every
    /// <see cref="ScyllaProbeInterval"/>. Called by the consume loop while the circuit is open, so
    /// the consumer doesn't re-subscribe (and start re-failing deliveries) before Scylla is back.
    /// </summary>
    private async Task WaitForScyllaHealthyAsync(CancellationToken ct)
    {
        _logger.LogWarning(
            "ScyllaMessageConsumer: circuit OPEN — consumption paused, probing Scylla every {Interval:0.0}s",
            ScyllaProbeInterval.TotalSeconds
        );

        while (!ct.IsCancellationRequested)
        {
            if (await ProbeScyllaAsync())
            {
                _logger.LogInformation(
                    "ScyllaMessageConsumer: Scylla healthy — circuit CLOSED, resuming consumption"
                );
                return;
            }

            try
            {
                await Task.Delay(ScyllaProbeInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// A single Scylla liveness probe: a bounded read against <c>system.local</c>. Any exception —
    /// including the <see cref="IScyllaSessionFactory.Session"/> getter throwing while Scylla is
    /// still down — means "not healthy yet". Never throws.
    /// </summary>
    private async Task<bool> ProbeScyllaAsync()
    {
        try
        {
            var statement = new SimpleStatement(
                "SELECT release_version FROM system.local"
            ).SetReadTimeoutMillis(ScyllaProbeReadTimeoutMs);

            await _sessionFactory.Session.ExecuteAsync(statement);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ScyllaMessageConsumer: Scylla probe failed — still down");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Per-event handlers — persist → broadcast (dedup runs once in the pre-check)
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

        // 2. Persist to ScyllaDB + create mention notifications
        await handler.HandleMessageSentAsync(evt);

        // 3. Broadcast authoritative message to channel subscribers
        // Fetch sender's display info for the client (best-effort; falls back to "Unknown").
        // Read-through the shared Redis cache first — this runs on every message, so avoiding the
        // per-message Postgres round-trip (and rented DbContext) is the point. A miss (or Redis
        // down) falls back to the repository and repopulates. The cache is invalidated on username
        // /avatar change, and a short TTL backstops any missed invalidation.
        string senderUsername = "Unknown";
        string? senderAvatarKey = null;
        try
        {
            var cache = services.GetRequiredService<IUserDisplayCache>();
            var display = await cache.GetAsync(evt.UserId);
            if (display is null)
            {
                var userRepo = services.GetRequiredService<IUserRepository>();
                var sender = await userRepo.GetByIdAsync(evt.UserId);
                display = new UserDisplay(sender?.UserName ?? "Unknown", sender?.AvatarKey);
                // Don't cache a not-yet-existing user's "Unknown" placeholder — only a real row.
                if (sender is not null)
                    await cache.SetAsync(evt.UserId, display.Value);
            }
            senderUsername = display.Value.Username;
            senderAvatarKey = display.Value.AvatarKey;
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
                Reactions: [],
                Forward: evt.Forward is null
                    ? null
                    : new ForwardSnapshotResponse(
                        evt.Forward.AuthorId,
                        evt.Forward.AuthorName,
                        evt.Forward.Content,
                        evt.Forward.SentAt
                    ),
                // Echo the sender's optimistic-send token so their client can reconcile in place.
                Nonce: evt.Nonce
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

    // A message can be edited many times, so the edit dedup key must be per-EDIT, not per-message —
    // keying on messageId alone made a second edit within the 60s dedup TTL look like a duplicate and
    // silently drop its broadcast (the message still edited in Scylla via the synchronous API write,
    // so only a refresh revealed it). Discriminating by the edit's timestamp (100ns ticks) keeps a
    // genuine RabbitMQ redelivery of the SAME edit deduped (same EditedAt → same key) while letting
    // distinct edits each broadcast.
    private static string EditedEventType(MessageEditedEvent evt) =>
        $"{IMessageDeduplicator.Edited}:{evt.EditedAt.UtcTicks}";

    private async Task HandleMessageEditedAsync(IMessageConsumerHandler handler, string body)
    {
        var evt = JsonSerializer.Deserialize<MessageEditedEvent>(body, JsonOptions);
        if (evt is null)
            return;

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

    // The integration-test environment (resolved once from the injected IHostEnvironment, which the
    // test WebApplicationFactory sets via UseEnvironment("Test")) so the out-of-order requeue can DLQ
    // after a single redelivery rather than burning the full backoff budget. Mirrors SearchIndexConsumer.
    private bool IsTestEnv() => _isTestEnv;

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
