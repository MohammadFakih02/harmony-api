namespace Harmony.Domain.Domain.Entities;

public class Message
{
    public long ChannelId { get; set; }
    public long MessageId { get; set; }
    public long UserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public List<long> AttachmentIds { get; set; } = [];
    public List<long> MentionIds { get; set; } = [];
    public long? ReplyToId { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsEdited { get; set; }
    public DateTime? EditedAt { get; set; }
    public string MessageType { get; set; } = "text";

    // Derived from Snowflake ID — not stored in Scylla
    public DateTime CreatedAt =>
        DateTimeOffset
            .FromUnixTimeMilliseconds((MessageId >> 22) + 1704067200000L) // custom epoch: 2024-01-01
            .UtcDateTime;
}
