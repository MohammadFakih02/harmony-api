using System.Text.Json.Serialization;

namespace Harmony.Application.DTOs.Requests;

public record SendMessageRequest(
    string Content,
    // MessageType is intentionally NOT client-supplied: user sends are always "text". System
    // notices (member_join / group_join / group_leave / pin) go through
    // IMessageService.PublishSystemMessageAsync, so a client can never spoof a system message.
    // The client sends snowflake ids as strings (full precision), so read them back from strings.
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? ReplyToId = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] List<long>? AttachmentIds = null
);

public record EditMessageRequest(string Content);

public record DeleteMessageRequest();
