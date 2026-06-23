using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Enums;
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
    private readonly IUserRepository _userRepository;
    private readonly IPermissionService _permissions;
    private readonly IFileAttachmentRepository _attachments;
    private readonly IDirectMessageRepository _dms;
    private readonly IUserBlockRepository _blocks;
    private readonly IPresenceService _presence;

    /// <summary>Max attachments per message (Discord parity).</summary>
    public const int MaxAttachments = 10;

    public MessageService(
        IChannelRepository channelRepository,
        IGuildRepository guildRepository,
        IMessagePublisher publisher,
        ISnowflakeIdGenerator snowflake,
        IMessageRepository messageRepository,
        IUserRepository userRepository,
        IPermissionService permissions,
        IFileAttachmentRepository attachments,
        IDirectMessageRepository dms,
        IUserBlockRepository blocks,
        IPresenceService presence
    )
    {
        _channelRepository = channelRepository;
        _guildRepository = guildRepository;
        _publisher = publisher;
        _snowflake = snowflake;
        _messageRepository = messageRepository;
        _userRepository = userRepository;
        _permissions = permissions;
        _attachments = attachments;
        _dms = dms;
        _blocks = blocks;
        _presence = presence;
    }

    /// <summary>
    /// Server-side `@mention` detection (NON-NEGOTIABLE #8 — never trust client-provided ids).
    /// Resolves candidates scoped to the channel's actual participants (guild members, or the
    /// DM peer), parses the content, and — for a guild channel — expands `@everyone`/`@here`
    /// into the member-id set if the actor holds `MentionEveryone`. If the actor lacks the
    /// permission, the literal text is simply not expanded; mentions are a notification side
    /// effect and must never block the send.
    /// </summary>
    private async Task<List<long>> ResolveMentionsAsync(
        string content,
        long? guildId,
        long channelId,
        long actorId,
        CancellationToken ct
    )
    {
        var guildContext = guildId.HasValue;
        var candidateIds = guildContext
            ? await _guildRepository.GetMemberIdsAsync(guildId!.Value)
            : await _dms.GetParticipantIdsAsync(channelId);

        var users = await _userRepository.GetByIdsAsync(candidateIds);
        var usersByUsernameLower = users.Values
            .Where(u => u.UserName is not null)
            .GroupBy(u => u.UserName!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Id);

        var parsed = MentionParser.Parse(content, usersByUsernameLower, guildContext);
        var mentionIds = parsed.UserIds;

        if (guildContext && (parsed.Everyone || parsed.Here))
        {
            var canMentionEveryone = await _permissions.HasAsync(
                actorId,
                guildId!.Value,
                Permission.MentionEveryone,
                channelId,
                ct
            );

            if (canMentionEveryone)
            {
                if (parsed.Everyone)
                {
                    foreach (var id in candidateIds)
                        mentionIds.Add(id);
                }
                else if (parsed.Here)
                {
                    var statuses = await _presence.GetStatusesAsync(candidateIds, ct);
                    foreach (var id in candidateIds)
                        if (statuses.TryGetValue(id, out var status) && status != "offline")
                            mentionIds.Add(id);
                }
            }
        }

        return mentionIds.ToList();
    }

    public async Task<SendMessageResponse> SendMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        SendMessageRequest request,
        CancellationToken ct = default
    )
    {
        if (guildId is { } gid)
        {
            // Verify channel exists and belongs to guild natively via repository
            var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, gid);
            if (channel is null)
                throw new KeyNotFoundException("Channel not found.");

            // Authorize: the caller must be able to view AND send in this channel. Resolved bits
            // apply the channel's overrides; non-members resolve to 0, so this subsumes the old
            // membership check. (ViewChannel | SendMessage are both in the @everyone default set.)
            const long sendMask = (long)(Permission.ViewChannel | Permission.SendMessage);
            var bits = await _permissions.ResolveAsync(userId, gid, channelId, ct);
            if ((bits & sendMask) != sendMask)
                throw new UnauthorizedAccessException(
                    "You do not have permission to send messages in this channel."
                );

            // Timeout gate (deliberately excluded from the cached resolver, §27): a member whose
            // CommunicationDisabledUntil is in the future cannot send, even with the permission.
            var member = await _guildRepository.GetMemberAsync(gid, userId);
            if (member?.CommunicationDisabledUntil is { } until
                && until > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new UnauthorizedAccessException("You are timed out and cannot send messages.");
        }
        else
        {
            // DM: no guild permissions — the caller must be a participant of the DM channel and
            // must not be blocked by (or blocking) the peer. Throws 404/403 as appropriate.
            await AuthorizeDmSendAsync(userId, channelId);
        }

        // Validate attachments: each must exist, be confirmed, be owned by the sender, and belong to
        // THIS channel (never trust client-provided ids — NON-NEGOTIABLE #8). Capped per message.
        var attachmentIds = request.AttachmentIds ?? [];
        if (attachmentIds.Count > MaxAttachments)
            throw new ArgumentException($"A message may have at most {MaxAttachments} attachments.");

        foreach (var attachmentId in attachmentIds)
        {
            var attachment = await _attachments.GetByIdAsync(attachmentId);
            if (attachment is null || !attachment.IsConfirmed)
                throw new ArgumentException("Attachment not found or not confirmed.");
            if (attachment.UploaderId != userId)
                throw new UnauthorizedAccessException("You can only attach files you uploaded.");
            // ChannelId uniquely identifies the container (guild channel or DM), so it is the
            // authoritative scope check for both guild messages and DMs.
            if (attachment.ChannelId != channelId)
                throw new ArgumentException("Attachment does not belong to this channel.");
        }

        // Content is required UNLESS the message carries at least one attachment (image-only message).
        var content = request.Content ?? string.Empty;
        if (content.Length > 2000)
            throw new ArgumentException("Message content must be 2000 characters or fewer.");
        if (string.IsNullOrWhiteSpace(content) && attachmentIds.Count == 0)
            throw new ArgumentException("Message must have content or at least one attachment.");

        var messageId = _snowflake.NextId();
        var sentAt = DateTimeOffset.UtcNow;
        var mentionIds = await ResolveMentionsAsync(content, guildId, channelId, userId, ct);

        await _publisher.PublishMessageSentAsync(
            new MessageSentEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                UserId: userId,
                Content: content,
                MessageType: request.MessageType ?? "text",
                AttachmentIds: attachmentIds,
                MentionIds: mentionIds,
                ReplyToId: request.ReplyToId,
                SentAt: sentAt
            ),
            ct
        );

        // A new message resurfaces a DM that either participant had hidden (§19).
        if (guildId is null)
            await _dms.UnhideAllAsync(channelId);

        return new SendMessageResponse(
            MessageId: messageId,
            ChannelId: channelId,
            GuildId: guildId,
            UserId: userId,
            Content: content,
            MessageType: request.MessageType ?? "text",
            ReplyToId: request.ReplyToId,
            MentionIds: mentionIds,
            AttachmentIds: attachmentIds,
            SentAt: sentAt.ToUnixTimeMilliseconds()
        );
    }

    public async Task DeleteMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    )
    {
        if (guildId is { } gid)
        {
            var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, gid);
            if (channel is null)
                throw new KeyNotFoundException("Channel not found.");
        }
        else
        {
            await GetDmChannelOrThrowAsync(channelId);
        }

        var message = await _messageRepository.GetByIdAsync(messageId, ct);
        if (message is null)
            throw new KeyNotFoundException("Message not found.");

        // CRITICAL SECURITY FIX: Prevent cross-channel message deletion
        if (message.ChannelId != channelId)
            throw new UnauthorizedAccessException(
                "Message does not belong to the specified channel."
            );

        // You may always delete your own message. In a guild, deleting another's requires
        // ManageMessages (owners/administrators resolve to all bits). A DM has no moderators,
        // so only the author may delete — and only a participant could own a message anyway.
        if (message.UserId != userId)
        {
            if (guildId is { } mgid
                && await _permissions.HasAsync(userId, mgid, Permission.ManageMessages, channelId, ct))
            {
                // moderator delete — allowed
            }
            else
            {
                throw new UnauthorizedAccessException(
                    "You do not have permission to delete this message."
                );
            }
        }

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
        long? guildId,
        long channelId,
        long messageId,
        EditMessageRequest request,
        CancellationToken ct = default
    )
    {
        // Edit is own-message-only for both guild and DM (ownership implies participation),
        // so no guild/DM authorization branch is needed beyond the author check below.
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

        // Capture the pre-edit mention set BEFORE overwriting Scylla — the consumer diffs
        // against this to notify only newly-added mentions. (It can't read the old set
        // itself: by the time it runs, the synchronous EditAsync below has replaced it.)
        var oldMentionIds = message.MentionIds.ToList();

        // Mentions are re-detected on every edit; the consumer notifies users newly added.
        var mentionIds = await ResolveMentionsAsync(request.Content, guildId, channelId, userId, ct);

        // 1. Synchronously update ScyllaDB
        await _messageRepository.EditAsync(messageId, channelId, request.Content, mentionIds, ct);

        // 2. Publish event to background queues (search index update)
        await _publisher.PublishMessageEditedAsync(
            new MessageEditedEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                EditedByUserId: userId,
                NewContent: request.Content,
                MentionIds: mentionIds,
                OldMentionIds: oldMentionIds,
                EditedAt: DateTimeOffset.UtcNow
            ),
            ct
        );
    }

    public async Task<ChannelMessagesResponse> GetChannelMessagesAsync(
        long userId,
        long? guildId,
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    )
    {
        if (guildId is { } gid)
        {
            var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, gid);
            if (channel is null)
                throw new KeyNotFoundException("Channel not found.");

            // Authorize: viewing channel history requires ViewChannel + ReadHistory (both in the
            // @everyone default). Channel overrides apply; non-members resolve to 0.
            const long readMask = (long)(Permission.ViewChannel | Permission.ReadHistory);
            var bits = await _permissions.ResolveAsync(userId, gid, channelId, ct);
            if ((bits & readMask) != readMask)
                throw new UnauthorizedAccessException(
                    "You do not have permission to view this channel."
                );
        }
        else
        {
            // DM: must be a participant. (Reading history is allowed even if blocked — blocking
            // hides the peer client-side; it doesn't revoke access to your own conversation.)
            await GetDmChannelOrThrowAsync(channelId);
            if (!await _dms.IsParticipantAsync(channelId, userId))
                throw new UnauthorizedAccessException("You are not a participant of this conversation.");
        }

        limit = Math.Clamp(limit, 1, 100);

        try
        {
            var messages = await _messageRepository.GetChannelMessagesAsync(
                channelId,
                limit,
                beforeMessageId,
                ct
            );

            var userIds = messages.Select(m => m.UserId).Distinct();
            var users = await _userRepository.GetByIdsAsync(userIds);

            return new ChannelMessagesResponse(
                messages.Select(m =>
                {
                    users.TryGetValue(m.UserId, out var user);
                    return new MessageResponse(
                        MessageId: m.MessageId,
                        ChannelId: m.ChannelId,
                        GuildId: guildId,
                        UserId: m.UserId,
                        Username: user?.UserName ?? "Unknown",
                        AvatarKey: user?.AvatarKey,
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
                    );
                }),
                Degraded: false
            );
        }
        catch (BrokenCircuitException)
        {
            return new ChannelMessagesResponse([], Degraded: true);
        }
    }

    // -------------------------------------------------------------------------
    // DM authorization helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts the channel exists and is a DM (no owning guild). Throws 404 otherwise,
    /// so a guild channel id can never be driven down the guild-less DM path.
    /// </summary>
    private async Task GetDmChannelOrThrowAsync(long channelId)
    {
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId is not null || channel.Type != "dm")
            throw new KeyNotFoundException("Channel not found.");
    }

    /// <summary>
    /// Authorizes a DM send: the channel must be a DM, the caller a participant, and the
    /// caller must not be blocked by (or blocking) the peer.
    /// </summary>
    private async Task AuthorizeDmSendAsync(long userId, long channelId)
    {
        await GetDmChannelOrThrowAsync(channelId);

        var participantIds = await _dms.GetParticipantIdsAsync(channelId);
        if (!participantIds.Contains(userId))
            throw new UnauthorizedAccessException("You are not a participant of this conversation.");

        // Blocking suppresses DMs in either direction.
        foreach (var peerId in participantIds)
        {
            if (peerId != userId && await _blocks.AreBlockedAsync(userId, peerId))
                throw new UnauthorizedAccessException("You cannot send messages to this user.");
        }
    }
}
