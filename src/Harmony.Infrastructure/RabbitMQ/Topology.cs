namespace Harmony.Infrastructure.RabbitMQ;

public static class Topology
{
    // Exchanges
    public const string MessageExchange = "harmony.messages";
    public const string NotificationExchange = "harmony.notifications";

    // Queues — fanout pattern: one queue per consumer type
    public const string ScyllaMessageQueue = "harmony.messages.scylla";
    public const string SearchIndexQueue = "harmony.messages.search";
    public const string NotificationQueue = "harmony.notifications.deliver";

    // Dead letter
    public const string DeadLetterExchange = "harmony.dead-letter";
    public const string DeadLetterQueue = "harmony.dead-letter.queue";

    // Routing keys
    public const string MessageSentKey = "message.sent";
    public const string MessageDeletedKey = "message.deleted";
    public const string MessageEditedKey = "message.edited";
    public const string NotificationKey = "notification.deliver";
}
