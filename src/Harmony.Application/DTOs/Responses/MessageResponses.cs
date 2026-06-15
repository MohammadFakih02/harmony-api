namespace Harmony.Application.DTOs.Responses;

public record SendMessageResponse(
    long MessageId,
    long ChannelId,
    long GuildId,
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
    long GuildId,
    long UserId,
    string Username,
    string? AvatarKey,
    string Content,
    string MessageType,
    bool IsDeleted,
    bool IsEdited,
    long? ReplyToId,
    List<long> MentionIds,
    List<long> AttachmentIds,
    long SentAt,
    long? EditedAt
);

public record ChannelMessagesResponse(
    IEnumerable<MessageResponse> Messages,
    bool Degraded
);
