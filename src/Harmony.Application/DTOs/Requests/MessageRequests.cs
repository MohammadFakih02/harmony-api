using System.Text.Json.Serialization;

namespace Harmony.Application.DTOs.Requests;

public record SendMessageRequest(
    string Content,
    // MessageType is intentionally NOT client-supplied: user sends are always "text". System
    // notices (member_join / group_join / group_leave / pin) go through
    // IMessageService.PublishSystemMessageAsync, so a client can never spoof a system message.
    // The client sends snowflake ids as strings (full precision), so read them back from strings.
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? ReplyToId = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] List<long>? AttachmentIds = null,
    // Opaque client-generated idempotency token, echoed back verbatim on the live MessageReceived
    // broadcast so the sender can reconcile its optimistic bubble against the authoritative copy
    // (even if the SignalR echo beats the POST/hub response) — killing the transient double-render.
    // Purely a correlation string: never trusted, never persisted, capped in the service.
    string? Nonce = null
);

/// <summary>
/// Forwards an existing message into the target channel/DM. The client supplies only *references*
/// (source channel + source message) plus an optional note and any re-uploaded attachments — the
/// server reads the original and builds the authoritative attributed snapshot, so a forward card
/// can never be forged (NON-NEGOTIABLE #8). Snowflake ids arrive as strings (full precision).
/// </summary>
public record ForwardMessageRequest(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long SourceChannelId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long SourceMessageId,
    string? Note = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] List<long>? AttachmentIds = null
);

public record EditMessageRequest(string Content);

public record DeleteMessageRequest();

/// <summary>Body for adding a reaction — the emoji travels in the body (never the route: Unicode in a
/// URL segment is fragile). Removal passes the emoji as a query-string parameter instead.</summary>
public record ReactionRequest(string Emoji);
