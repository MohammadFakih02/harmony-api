namespace Harmony.Infrastructure.RabbitMQ;

public static class Topology
{
    // Exchanges
    public const string MessageExchange = "harmony.messages";
    public const string NotificationExchange = "harmony.notifications";

    // Dead letter exchange — receives nacked messages
    public const string DeadLetterExchange = "harmony.dead-letter";
    public const string DeadLetterQueue = "harmony.dead-letter.queue";

    // Main queues
    public const string ScyllaMessageQueue = "harmony.messages.scylla";
    public const string SearchIndexQueue = "harmony.messages.search";
    public const string NotificationQueue = "harmony.notifications.deliver";

    // Retry queues — passive parking lots with TTL, no consumers
    // Messages sit here for 15 seconds then auto-republish to main exchange
    public const string ScyllaRetryQueue = "harmony.messages.scylla.retry";
    public const string SearchRetryQueue = "harmony.messages.search.retry";

    // Routing keys
    public const string MessageSentKey = "message.sent";
    public const string MessageDeletedKey = "message.deleted";
    public const string MessageEditedKey = "message.edited";
    public const string NotificationKey = "notification.deliver";

    // Retry TTL — must be > circuit breaker duration (10s)
    public const int RetryTtlMs = 15_000;
}
