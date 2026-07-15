using System.Text.Json.Serialization;

namespace Harmony.Application.DTOs.Responses;

public record SendMessageResponse(
    long MessageId,
    long ChannelId,
    long? GuildId,
    long UserId,
    string Content,
    string MessageType,
    long? ReplyToId,
    List<long> MentionIds,
    List<long> AttachmentIds,
    long SentAt
);

public record MessageResponse(
    long MessageId,
    long ChannelId,
    long? GuildId,
    long UserId,
    string Username,
    string? AvatarKey,
    string Content,
    string MessageType,
    bool IsDeleted,
    bool IsEdited,
    long? ReplyToId,
    List<long> MentionIds,
    // Snowflake ids → JSON strings so the browser keeps full precision (the attachment
    // renderer fetches GET /files/{id} with these; a rounded id would 404). AllowReadingFromString
    // is paired with it so the type round-trips: WriteAsString alone emits a string this same
    // record then refuses to parse back, which breaks any consumer that deserializes it (the
    // integration suite does). It does not change a single emitted byte.
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString
    )]
        List<long> AttachmentIds,
    long SentAt,
    long? EditedAt,
    // Aggregated reaction pills (emoji → count + whether the requester reacted). Empty for a
    // brand-new message (the live MessageReceived broadcast carries none — reactions arrive via
    // their own ReactionAdded/Removed events).
    IReadOnlyList<ReactionSummaryResponse> Reactions,
    // Server-authoritative snapshot of the original message when this message is a forward.
    // Null for ordinary messages.
    ForwardSnapshotResponse? Forward = null,
    // Echo of the sender's optimistic-send idempotency token — present ONLY on the live
    // MessageReceived broadcast (never persisted, so historical reads carry null). Lets the
    // sender's client replace its optimistic bubble in place regardless of echo/POST ordering.
    string? Nonce = null
);

/// <summary>One reaction bucket on a message: the emoji, how many users reacted, and whether the
/// requesting user is one of them (drives the highlighted pill state).</summary>
public record ReactionSummaryResponse(string Emoji, int Count, bool MeReacted);

/// <summary>The attributed-quote snapshot rendered above a forwarded message.</summary>
public record ForwardSnapshotResponse(
    // Paired with AllowReadingFromString for the same round-trip reason as AttachmentIds above.
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString
    )]
        long AuthorId,
    string AuthorName,
    string Content,
    long SentAt
);

public record ChannelMessagesResponse(
    IEnumerable<MessageResponse> Messages,
    bool Degraded,
    // Whole seconds left on the caller's slowmode cooldown in this channel (0 = none). Populated only
    // on a latest/open load so the client can restore the countdown across leave/rejoin.
    int SlowmodeRemainingSeconds = 0
);

/// <summary>
/// A pinned message: the full <see cref="MessageResponse"/> (so the client renders it exactly like a
/// message) plus who pinned it and when. <c>PinnedAt</c> equals the message id (the pin's Scylla
/// clustering key), unix-ms-derivable via the snowflake epoch on the client if needed for display.
/// </summary>
public record PinnedMessageResponse(
    MessageResponse Message,
    long PinnedBy,
    long PinnedAt
);
