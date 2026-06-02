namespace Harmony.Domain.Domain.Entities;

public class PinnedMessage
{
    public long ChannelId { get; set; }
    public long PinnedAt { get; set; } // Snowflake ID used as clustering key
    public long MessageId { get; set; }
    public long PinnedBy { get; set; }
}
