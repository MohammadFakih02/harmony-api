namespace Harmony.Core.DTOs.Responses;

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
