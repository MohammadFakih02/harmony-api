using Harmony.Core.DTOs.Requests;
using Harmony.Core.DTOs.Responses;
using Harmony.Core.Interfaces;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Core.Interfaces.Services;
using Harmony.Core.Services;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Services;

public class MessageService : IMessageService
{
    private readonly HarmonyDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IMessageRepository _messageRepository;

    public MessageService(
        HarmonyDbContext db,
        IMessagePublisher publisher,
        ISnowflakeIdGenerator snowflake,
        IMessageRepository messageRepository
    )
    {
        _db = db;
        _publisher = publisher;
        _snowflake = snowflake;
        _messageRepository = messageRepository;
    }

    public async Task<SendMessageResponse> SendMessageAsync(
        long userId,
        long guildId,
        long channelId,
        SendMessageRequest request,
        CancellationToken ct = default
    )
    {
        // Verify channel exists and belongs to guild
        var channel = await _db.Channels.FirstOrDefaultAsync(
            c => c.Id == channelId && c.GuildId == guildId,
            ct
        );

        if (channel is null)
            throw new KeyNotFoundException("Channel not found.");

        // Verify user is a member of the guild
        var isMember = await _db.GuildMembers.AnyAsync(
            m => m.GuildId == guildId && m.UserId == userId,
            ct
        );

        if (!isMember)
            throw new UnauthorizedAccessException("You are not a member of this guild.");

        // Validate content
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 2000)
            throw new ArgumentException("Message content must be between 1 and 2000 characters.");

        var messageId = _snowflake.NextId();
        var sentAt = DateTimeOffset.UtcNow;

        await _publisher.PublishMessageSentAsync(
            new MessageSentEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                UserId: userId,
                Content: request.Content,
                MessageType: request.MessageType ?? "text",
                AttachmentIds: request.AttachmentIds ?? [],
                MentionIds: request.MentionIds ?? [],
                ReplyToId: request.ReplyToId,
                SentAt: sentAt
            ),
            ct
        );

        return new SendMessageResponse(
            MessageId: messageId,
            ChannelId: channelId,
            GuildId: guildId,
            UserId: userId,
            Content: request.Content,
            MessageType: request.MessageType ?? "text",
            ReplyToId: request.ReplyToId,
            MentionIds: request.MentionIds ?? [],
            AttachmentIds: request.AttachmentIds ?? [],
            SentAt: sentAt.ToUnixTimeMilliseconds()
        );
    }

    public async Task DeleteMessageAsync(
        long userId,
        long guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    )
    {
        var channel = await _db.Channels.FirstOrDefaultAsync(
            c => c.Id == channelId && c.GuildId == guildId,
            ct
        );

        if (channel is null)
            throw new KeyNotFoundException("Channel not found.");

        var message = await _messageRepository.GetByIdAsync(messageId, ct);

        if (message is null)
            throw new KeyNotFoundException("Message not found.");

        var isOwner = await _db.GuildMembers.AnyAsync(
            m => m.GuildId == guildId && m.UserId == userId && m.IsOwner,
            ct
        );

        if (message.UserId != userId && !isOwner)
            throw new UnauthorizedAccessException(
                "You do not have permission to delete this message."
            );

        await _publisher.PublishMessageDeletedAsync(
            new MessageDeletedEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                DeletedByUserId: userId,
                DeletedAt: DateTimeOffset.UtcNow
            ),
            ct
        );
    }

    public async Task EditMessageAsync(
        long userId,
        long guildId,
        long channelId,
        long messageId,
        EditMessageRequest request,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 2000)
            throw new ArgumentException("Message content must be between 1 and 2000 characters.");

        var message = await _messageRepository.GetByIdAsync(messageId, ct);

        if (message is null)
            throw new KeyNotFoundException("Message not found.");

        if (message.UserId != userId)
            throw new UnauthorizedAccessException("You can only edit your own messages.");

        await _publisher.PublishMessageEditedAsync(
            new MessageEditedEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                EditedByUserId: userId,
                NewContent: request.Content,
                EditedAt: DateTimeOffset.UtcNow
            ),
            ct
        );
    }

    public async Task<IEnumerable<MessageResponse>> GetChannelMessagesAsync(
        long userId,
        long guildId,
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    )
    {
        // Verify channel exists and belongs to guild
        var channel = await _db.Channels.FirstOrDefaultAsync(
            c => c.Id == channelId && c.GuildId == guildId,
            ct
        );

        if (channel is null)
            throw new KeyNotFoundException("Channel not found.");

        // Verify user is a member of the guild
        var isMember = await _db.GuildMembers.AnyAsync(
            m => m.GuildId == guildId && m.UserId == userId,
            ct
        );

        if (!isMember)
            throw new UnauthorizedAccessException("You are not a member of this guild.");

        limit = Math.Clamp(limit, 1, 100);
        var messages = await _messageRepository.GetChannelMessagesAsync(
            channelId,
            limit,
            beforeMessageId,
            ct
        );

        return messages.Select(m => new MessageResponse(
            MessageId: m.MessageId,
            ChannelId: m.ChannelId,
            GuildId: guildId,
            UserId: m.UserId,
            Content: m.IsDeleted ? string.Empty : m.Content,
            MessageType: m.MessageType,
            IsDeleted: m.IsDeleted,
            IsEdited: m.IsEdited,
            ReplyToId: m.ReplyToId,
            MentionIds: m.IsDeleted ? [] : m.MentionIds,
            AttachmentIds: m.IsDeleted ? [] : m.AttachmentIds,
            SentAt: ((DateTimeOffset)m.CreatedAt).ToUnixTimeMilliseconds(),
            EditedAt: m.EditedAt.HasValue
                ? ((DateTimeOffset)m.EditedAt.Value).ToUnixTimeMilliseconds()
                : null
        ));
    }
}
