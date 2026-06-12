using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Services;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Polly.CircuitBreaker;

namespace Harmony.Application.Services;

public class MessageService : IMessageService
{
    private readonly IChannelRepository _channelRepository;
    private readonly IGuildRepository _guildRepository;
    private readonly IMessagePublisher _publisher;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IMessageRepository _messageRepository;

    public MessageService(
        IChannelRepository channelRepository,
        IGuildRepository guildRepository,
        IMessagePublisher publisher,
        ISnowflakeIdGenerator snowflake,
        IMessageRepository messageRepository
    )
    {
        _channelRepository = channelRepository;
        _guildRepository = guildRepository;
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
        // Verify channel exists and belongs to guild natively via repository
        var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, guildId);
        if (channel is null)
            throw new KeyNotFoundException("Channel not found.");

        // Verify user is a member of the guild natively via repository
        var isMember = await _guildRepository.IsMemberAsync(guildId, userId);
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
        var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, guildId);
        if (channel is null)
            throw new KeyNotFoundException("Channel not found.");

        var message = await _messageRepository.GetByIdAsync(messageId, ct);
        if (message is null)
            throw new KeyNotFoundException("Message not found.");

        // CRITICAL SECURITY FIX: Prevent cross-channel message deletion
        if (message.ChannelId != channelId)
            throw new UnauthorizedAccessException(
                "Message does not belong to the specified channel."
            );

        var guild = await _guildRepository.GetByIdAsync(guildId);
        var isOwner = guild is not null && guild.OwnerId == userId;

        if (message.UserId != userId && !isOwner)
            throw new UnauthorizedAccessException(
                "You do not have permission to delete this message."
            );

        // 1. Synchronously update ScyllaDB
        await _messageRepository.DeleteAsync(messageId, channelId, ct);

        // 2. Publish event to background queues (search index update)
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

        // CRITICAL SECURITY FIX: Prevent cross-channel message editing
        if (message.ChannelId != channelId)
            throw new UnauthorizedAccessException(
                "Message does not belong to the specified channel."
            );

        if (message.UserId != userId)
            throw new UnauthorizedAccessException("You can only edit your own messages.");

        // 1. Synchronously update ScyllaDB
        await _messageRepository.EditAsync(messageId, channelId, request.Content, ct);

        // 2. Publish event to background queues (search index update)
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

    public async Task<ChannelMessagesResponse> GetChannelMessagesAsync(
        long userId,
        long guildId,
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    )
    {
        var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, guildId);
        if (channel is null)
            throw new KeyNotFoundException("Channel not found.");

        var isMember = await _guildRepository.IsMemberAsync(guildId, userId);
        if (!isMember)
            throw new UnauthorizedAccessException("You are not a member of this guild.");

        limit = Math.Clamp(limit, 1, 100);

        try
        {
            var messages = await _messageRepository.GetChannelMessagesAsync(
                channelId,
                limit,
                beforeMessageId,
                ct
            );

            return new ChannelMessagesResponse(
                messages.Select(m => new MessageResponse(
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
                )),
                Degraded: false
            );
        }
        catch (BrokenCircuitException)
        {
            return new ChannelMessagesResponse([], Degraded: true);
        }
    }
}
