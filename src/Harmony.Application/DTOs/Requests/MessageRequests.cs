namespace Harmony.Application.DTOs.Requests;

public record SendMessageRequest(
    string Content,
    string? MessageType = "text",
    long? ReplyToId = null,
    List<long>? MentionIds = null,
    List<long>? AttachmentIds = null
);

public record EditMessageRequest(string Content);

public record DeleteMessageRequest();
