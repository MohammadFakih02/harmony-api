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
    // renderer fetches GET /files/{id} with these; a rounded id would 404).
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] List<long> AttachmentIds,
    long SentAt,
    long? EditedAt,
    // Aggregated reaction pills (emoji → count + whether the requester reacted). Empty for a
    // brand-new message (the live MessageReceived broadcast carries none — reactions arrive via
    // their own ReactionAdded/Removed events).
    IReadOnlyList<ReactionSummaryResponse> Reactions
);

/// <summary>One reaction bucket on a message: the emoji, how many users reacted, and whether the
/// requesting user is one of them (drives the highlighted pill state).</summary>
public record ReactionSummaryResponse(string Emoji, int Count, bool MeReacted);

public record ChannelMessagesResponse(
    IEnumerable<MessageResponse> Messages,
    bool Degraded
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
