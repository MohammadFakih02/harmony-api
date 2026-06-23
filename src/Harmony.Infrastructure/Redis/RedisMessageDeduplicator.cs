using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="IMessageDeduplicator"/>.
///
/// Uses the atomic <c>SET key value NX PX milliseconds</c> command:
///   NX  — only set if key does Not eXist
///   PX  — expiry in milliseconds
///
/// This is a single round-trip with no race condition between check and claim.
/// If two consumer instances race on the same message, exactly one wins the
/// SET NX and processes it; the other sees false and skips silently.
///
/// When Redis is unavailable (<see cref="IConnectionMultiplexer"/> is null or
/// the command throws), the guard fails OPEN — the message is processed normally.
/// This is intentional: losing deduplication briefly is far preferable to
/// dropping messages. The ScyllaDB-level idempotency checks in
/// <see cref="MessageConsumerHandler"/> act as the second line of defence.
/// </summary>
public sealed class RedisMessageDeduplicator : IMessageDeduplicator
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    // Short-lived counter for bounding out-of-order requeues. Comfortably outlives the
    // requeue budget (MaxOutOfOrderRequeues × backoff ≈ 16s) without lingering long enough
    // to interfere with a genuinely distinct later edit of the same message.
    private static readonly TimeSpan RequeueCountTtl = TimeSpan.FromMinutes(2);

    private readonly IRedisConnectionProvider _redisProvider;

    private readonly ILogger<RedisMessageDeduplicator> _logger;

    public RedisMessageDeduplicator(
        IRedisConnectionProvider redisProvider,
        ILogger<RedisMessageDeduplicator> logger
    )
    {
        _redisProvider = redisProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> IsDuplicateAsync(
        string eventType,
        long messageId,
        CancellationToken ct = default
    )
    {
        // Fail open: if Redis is not configured, treat every message as new.
        if (!_redisProvider.IsConnected)
        {
            _logger.LogDebug(
                "Deduplicator: Redis unavailable — failing open for {EventType}:{MessageId}",
                eventType, messageId
            );
            return false;
        }

        var key = BuildKey(eventType, messageId);

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();

            // SET key "1" NX PX 60000
            // Returns true  → key was set   → this is the FIRST processing → not a duplicate
            // Returns false → key existed   → already processed            → IS a duplicate
            var wasSet = await db.StringSetAsync(key, "1", Ttl, When.NotExists, CommandFlags.None);

            if (!wasSet)
            {
                _logger.LogWarning(
                    "Deduplicator: duplicate detected — skipping {EventType}:{MessageId}",
                    eventType,
                    messageId
                );
            }

            // wasSet=true  → not a duplicate → return false
            // wasSet=false → IS a duplicate  → return true
            return !wasSet;
        }
        catch (Exception ex)
        {
            // Fail open on any Redis error
            _logger.LogError(
                ex,
                "Deduplicator: Redis error for {EventType}:{MessageId} — failing open",
                eventType,
                messageId
            );
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task ClearAsync(string eventType, long messageId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        var key = BuildKey(eventType, messageId);
        try
        {
            await _redisProvider.Connection!.GetDatabase().KeyDeleteAsync(key);
            _logger.LogDebug(
                "Deduplicator: cleared key for {EventType}:{MessageId}",
                eventType, messageId
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deduplicator: failed to clear key for {EventType}:{MessageId} — ignoring",
                eventType, messageId
            );
        }
    }

    /// <inheritdoc/>
    public async Task<long> IncrementRequeueCountAsync(
        string eventType,
        long messageId,
        CancellationToken ct = default
    )
    {
        // Fail open: without Redis we can't bound, so return 0 → caller keeps requeuing.
        // RabbitMQ's queue x-message-ttl remains the ultimate backstop in that case.
        if (!_redisProvider.IsConnected)
            return 0;

        var key = BuildRequeueKey(eventType, messageId);
        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var count = await db.StringIncrementAsync(key);

            // Set the TTL only on the first increment so the window doesn't keep extending
            // with each requeue.
            if (count == 1)
                await db.KeyExpireAsync(key, RequeueCountTtl);

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Deduplicator: requeue-count increment failed for {EventType}:{MessageId} — failing open (0)",
                eventType,
                messageId
            );
            return 0;
        }
    }

    /// <summary>
    /// Builds the Redis key for a given event type and message ID.
    /// Format: <c>dedup:msg:{eventType}:{messageId}</c>
    /// </summary>
    public static string BuildKey(string eventType, long messageId) =>
        $"dedup:msg:{eventType}:{messageId}";

    /// <summary>
    /// Builds the Redis key for the out-of-order requeue counter.
    /// Format: <c>requeue:msg:{eventType}:{messageId}</c>
    /// </summary>
    public static string BuildRequeueKey(string eventType, long messageId) =>
        $"requeue:msg:{eventType}:{messageId}";
}
