namespace Harmony.API.Hubs;

public interface IChatClient
{
    // Message events
    Task MessageReceived(MessageReceivedPayload payload);
    Task MessageEdited(MessageEditedPayload payload);
    Task MessageDeleted(MessageDeletedPayload payload);

    // Typing events
    Task TypingStarted(TypingPayload payload);
    Task TypingStopped(TypingPayload payload);

    // Presence events
    Task PresenceUpdated(PresencePayload payload);

    // Unread count events
    Task UnreadCountUpdated(UnreadCountPayload payload);

    // Error feedback to caller only
    Task Error(string message);
}

// --------------- Payloads ---------------

public record MessageReceivedPayload(
    long MessageId,
    long ChannelId,
    long GuildId,
    long UserId,
    string Username,
    string? AvatarKey,
    string Content,
    string MessageType,
    long? ReplyToId,
    List<long> AttachmentIds,
    List<long> MentionIds,
    long SentAt
);

public record MessageEditedPayload(
    long MessageId,
    long ChannelId,
    long GuildId,
    string NewContent,
    long EditedAt
);

public record MessageDeletedPayload(long MessageId, long ChannelId, long GuildId);

public record TypingPayload(long UserId, string Username, long ChannelId, long GuildId);

public record PresencePayload(long UserId, string Status); // "online" | "idle" | "dnd" | "offline"

public record UnreadCountPayload(long ChannelId, int UnreadCount);
