namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Redis-backed sliding-window deduplication gate for RabbitMQ message events.
///
/// Sits at the broker layer (ScyllaMessageConsumer) — checked before any
/// handler or database operation runs. Uses atomic SET NX PX so there is
/// no race condition between the check and the claim.
///
/// Key format:  dedup:msg:{eventType}:{messageId}
/// TTL:         60 seconds — safely beyond the 3-retry exponential backoff
///              window (~14s) but short enough to not waste Redis memory.
///
/// Event types are kept separate so a sent → edited → deleted sequence on
/// the same messageId is not incorrectly blocked by the sent dedup key.
/// </summary>
public interface IMessageDeduplicator
{
    /// <summary>Event type constant for MessageSentEvent.</summary>
    const string Sent = "sent";

    /// <summary>Event type constant for MessageDeletedEvent.</summary>
    const string Deleted = "deleted";

    /// <summary>Event type constant for MessageEditedEvent.</summary>
    const string Edited = "edited";

    /// <summary>
    /// Atomically claims the dedup key for this event.
    ///
    /// Returns <c>true</c> if the event was already processed (key existed) — caller should skip.
    /// Returns <c>false</c> if this is the first time (key was set) — caller should process.
    /// </summary>
    /// <param name="eventType">Use the constants on this interface: Sent, Deleted, Edited.</param>
    /// <param name="messageId">The Snowflake message ID from the event.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsDuplicateAsync(string eventType, long messageId, CancellationToken ct = default);

    /// <summary>
    /// Clears the dedup key for this event, allowing a future redelivery to be processed.
    ///
    /// Called on terminal failure (after all retries exhausted) so a genuine RabbitMQ
    /// redelivery is not wrongly swallowed as a duplicate. Scylla writes are idempotent
    /// upserts, so reprocessing is safe. Fails open (silently) if Redis is unavailable.
    /// </summary>
    /// <param name="eventType">Use the constants on this interface: Sent, Deleted, Edited.</param>
    /// <param name="messageId">The Snowflake message ID from the event.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ClearAsync(string eventType, long messageId, CancellationToken ct = default);

    /// <summary>
    /// Atomically increments and returns the out-of-order requeue attempt count for an event.
    ///
    /// Used by the consumer to DLQ an event after a bounded number of requeues rather than
    /// looping forever against a MessageSent that will never land (e.g. one that permanently
    /// failed and was itself DLQ'd). Backed by a short-TTL Redis counter
    /// (key: <c>requeue:msg:{eventType}:{messageId}</c>).
    ///
    /// Fails open returning <c>0</c> (keep requeuing) when Redis is unavailable — losing the
    /// bound briefly is preferable to wrongly DLQ-ing a recoverable event, and RabbitMQ's
    /// queue-level message TTL remains the ultimate backstop.
    /// </summary>
    /// <param name="eventType">Use the constants on this interface: Sent, Deleted, Edited.</param>
    /// <param name="messageId">The Snowflake message ID from the event.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> IncrementRequeueCountAsync(
        string eventType,
        long messageId,
        CancellationToken ct = default
    );
}
