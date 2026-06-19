using System.Text.Json.Serialization;

namespace Harmony.Application.DTOs.Requests;

public record SendMessageRequest(
    string Content,
    string? MessageType = "text",
    // The client sends snowflake ids as strings (full precision), so read them back from strings.
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? ReplyToId = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] List<long>? MentionIds = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] List<long>? AttachmentIds = null
);

public record EditMessageRequest(string Content);

public record DeleteMessageRequest();
