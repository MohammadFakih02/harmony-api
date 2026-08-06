namespace Harmony.Domain.Domain.Entities;

/// <summary>
/// A single user's reaction to a message, stored in PostgreSQL (messages themselves live in Scylla,
/// so <see cref="MessageId"/>/<see cref="ChannelId"/> are Snowflakes with no FK — same cross-store
/// precedent as MessagesSearch / Notifications.message_id / PushOutbox). The composite PK
/// (message, emoji, user) makes a re-reaction a harmless idempotent upsert.
///
/// <see cref="Emoji"/> is a forward-compat token: a Unicode emoji character today; a
/// <c>custom:{emojiId}</c> string once custom guild emoji land (slice 3) — hence the 64-char width.
/// </summary>
public class MessageReaction
{
    public long MessageId { get; set; }
    public long ChannelId { get; set; }
    public string Emoji { get; set; } = null!;
    public long UserId { get; set; }
    public long CreatedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}

/// <summary>
/// An aggregated reaction bucket for one emoji on one message: how many users reacted, and whether
/// the requesting viewer is one of them. The unit the client renders as a reaction pill.
/// </summary>
public record ReactionSummary(string Emoji, int Count, bool MeReacted);
