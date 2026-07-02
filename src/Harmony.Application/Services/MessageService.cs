using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
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
    private readonly IAuditLogService _auditLog;
    private readonly IHubBroadcaster _broadcaster;
    private readonly IRoleRepository _roles;

    /// <summary>Max attachments per message (Discord parity).</summary>
    public const int MaxAttachments = 10;

    /// <summary>Max pinned messages per channel (Discord parity).</summary>
    public const int MaxPinsPerChannel = 50;

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
        IPresenceService presence,
        IAuditLogService auditLog,
        IHubBroadcaster broadcaster,
        IRoleRepository roles
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
        _auditLog = auditLog;
        _broadcaster = broadcaster;
        _roles = roles;
    }

    /// <summary>
    /// Server-side `@mention` detection (NON-NEGOTIABLE #8 — never trust client-provided ids).
    /// Resolves candidates scoped to the channel's actual participants: guild members (by username
    /// AND server nickname) plus the guild's roles, or — in a DM — just the peer's username. Then
    /// expands broadcast/role mentions: `@everyone`/`@here` if the actor holds `MentionEveryone`, and
    /// a `@role` into that role's members if the role `IsMentionable` OR the actor holds
    /// `MentionEveryone`. If the actor lacks the permission the literal text is simply not expanded;
    /// mentions are a notification side effect and must never block the send.
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
        // name -> userId: usernames first, then (guild only) server nicknames on top — so a member
        // is mentionable by either. Last write wins on a collision (a harmless edge).
        var usersByNameLower = new Dictionary<string, long>();
        foreach (var u in users.Values)
            if (u.UserName is not null)
                usersByNameLower[u.UserName.ToLowerInvariant()] = u.Id;

        List<Role> roles = [];
        Dictionary<string, long>? rolesByNameLower = null;
        if (guildContext)
        {
            var members = await _guildRepository.GetMembersAsync(guildId!.Value);
            foreach (var m in members)
                if (!string.IsNullOrWhiteSpace(m.Nickname))
                    usersByNameLower[m.Nickname.ToLowerInvariant()] = m.UserId;

            roles = await _roles.GetByGuildAsync(guildId.Value);
            rolesByNameLower = new Dictionary<string, long>();
            foreach (var r in roles)
                // The default (@everyone) role is addressed via the literal @everyone token, not by name.
                if (!r.IsDefault)
                    rolesByNameLower[r.Name.ToLowerInvariant()] = r.Id;
        }

        var parsed = MentionParser.Parse(content, usersByNameLower, guildContext, rolesByNameLower);
        var mentionIds = parsed.UserIds;

        if (!guildContext)
            return mentionIds.ToList();

        // MentionEveryone is the shared gate for @everyone/@here AND non-mentionable roles — resolve
        // it once, only if some broadcast/role expansion is actually needed.
        var needsPermission =
            parsed.Everyone || parsed.Here || parsed.RoleIds.Count > 0;
        var canMentionEveryone =
            needsPermission
            && await _permissions.HasAsync(
                actorId,
                guildId!.Value,
                Permission.MentionEveryone,
                channelId,
                ct
            );

        if (parsed.Everyone && canMentionEveryone)
        {
            foreach (var id in candidateIds)
                mentionIds.Add(id);
        }
        else if (parsed.Here && canMentionEveryone)
        {
            var statuses = await _presence.GetStatusesAsync(candidateIds, ct);
            foreach (var id in candidateIds)
                if (statuses.TryGetValue(id, out var status) && status != "offline")
                    mentionIds.Add(id);
        }

        if (parsed.RoleIds.Count > 0)
        {
            var rolesById = roles.ToDictionary(r => r.Id);
            foreach (var roleId in parsed.RoleIds)
            {
                if (!rolesById.TryGetValue(roleId, out var role))
                    continue;
                if (!role.IsMentionable && !canMentionEveryone)
                    continue;
                foreach (var memberId in await _roles.GetMemberIdsWithRoleAsync(guildId!.Value, roleId))
                    mentionIds.Add(memberId);
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

    /// <inheritdoc />
    public async Task<long> PublishSystemMessageAsync(
        long guildId,
        long channelId,
        long authorUserId,
        string messageType,
        string content,
        CancellationToken ct = default
    )
    {
        var messageId = _snowflake.NextId();

        await _publisher.PublishMessageSentAsync(
            new MessageSentEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                UserId: authorUserId,
                Content: content,
                MessageType: messageType,
                AttachmentIds: [],
                MentionIds: [],
                ReplyToId: null,
                SentAt: DateTimeOffset.UtcNow
            ),
            ct
        );

        return messageId;
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
        var isModeratorDelete = false;
        if (message.UserId != userId)
        {
            if (guildId is { } mgid
                && await _permissions.HasAsync(userId, mgid, Permission.ManageMessages, channelId, ct))
            {
                // moderator delete — allowed
                isModeratorDelete = true;
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

        // 1b. A deleted message can no longer be pinned — drop any pin row so the pins panel and
        //     the 50-cap stay honest. Idempotent (a tombstone if it wasn't pinned) and best-effort:
        //     a leftover pin row is harmless (bounded by the cap, purged on channel delete), and the
        //     client already drops the pin locally off the MessageDeleted broadcast.
        try
        {
            await _messageRepository.UnpinAsync(channelId, messageId, ct);
        }
        catch
        {
            // ignore — pin cleanup must never fail an otherwise-successful delete
        }

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

        // 3. Audit only a *moderator* deleting someone else's message (flow #12). Own-message
        //    deletes and DMs (no guild, no moderators) write nothing. Best-effort by contract.
        if (isModeratorDelete && guildId is { } agid)
        {
            await _auditLog.LogAsync(
                agid,
                userId,
                AuditLogAction.MessageDelete,
                targetId: messageId,
                changes: new { channelId, authorId = message.UserId },
                ct: ct
            );
        }
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
        long? aroundMessageId = null,
        long? afterMessageId = null,
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
            // Cursor precedence (mutually exclusive from the client): a jump target (around) loads a
            // window centred on that message; after = scroll-down "load newer"; else the before page.
            IEnumerable<Message> messages;
            if (aroundMessageId is { } around)
                messages = await _messageRepository.GetMessagesAroundAsync(channelId, around, limit, ct);
            else if (afterMessageId is { } after)
                messages = await _messageRepository.GetMessagesAfterAsync(channelId, after, limit, ct);
            else
                messages = await _messageRepository.GetChannelMessagesAsync(
                    channelId,
                    limit,
                    beforeMessageId,
                    ct
                );

            var userIds = messages.Select(m => m.UserId).Distinct();
            var users = await _userRepository.GetByIdsAsync(userIds);

            return new ChannelMessagesResponse(
                messages.Select(m => MapMessage(m, guildId, users)),
                Degraded: false
            );
        }
        catch (BrokenCircuitException)
        {
            return new ChannelMessagesResponse([], Degraded: true);
        }
    }

    /// <summary>
    /// Projects a Scylla <see cref="Message"/> into the wire <see cref="MessageResponse"/>, resolving
    /// the sender's identity from a pre-fetched batch (no N+1). <paramref name="guildId"/> is the
    /// request scope (null for DMs) — a message row doesn't carry its guild. Shared by the channel
    /// history read and the pins list so both render identically.
    /// </summary>
    private static MessageResponse MapMessage(
        Message m,
        long? guildId,
        Dictionary<long, User> users
    )
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
    }

    // -------------------------------------------------------------------------
    // Pins
    // -------------------------------------------------------------------------

    public async Task PinMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    )
    {
        await AuthorizePinActionAsync(userId, guildId, channelId, ct);

        var message = await _messageRepository.GetByIdAsync(messageId, ct);
        if (message is null)
            throw new KeyNotFoundException("Message not found.");
        if (message.ChannelId != channelId)
            throw new UnauthorizedAccessException(
                "Message does not belong to the specified channel."
            );
        if (message.IsDeleted)
            throw new ArgumentException("You cannot pin a deleted message.");

        var pinned = (await _messageRepository.GetPinnedAsync(channelId, ct)).ToList();
        // Idempotent: the pin's clustering key IS the message id, so re-pinning is a harmless
        // upsert — short-circuit so it neither errors on the cap nor duplicates the side effects.
        if (pinned.Any(p => p.MessageId == messageId))
            return;
        if (pinned.Count >= MaxPinsPerChannel)
            throw new ArgumentException(
                $"This channel has reached the maximum of {MaxPinsPerChannel} pins."
            );

        await _messageRepository.PinAsync(channelId, messageId, userId, ct);

        // Guild only: post a "pinned a message" system notice (author = the pinner) and audit.
        // DMs have neither system-message infra nor an audit log.
        if (guildId is { } gid)
        {
            await PublishSystemMessageAsync(gid, channelId, userId, "pin", string.Empty, ct);
            await _auditLog.LogAsync(
                gid,
                userId,
                AuditLogAction.MessagePin,
                targetId: messageId,
                changes: new { channelId },
                ct: ct
            );
        }

        await _broadcaster.BroadcastMessagePinnedAsync(new MessagePinPayload(messageId, channelId), ct);
    }

    public async Task UnpinMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    )
    {
        await AuthorizePinActionAsync(userId, guildId, channelId, ct);

        // pinned_at == messageId (the pin's clustering key), so unpin keys on the message id.
        // Idempotent — a DELETE on an absent clustering key is a harmless tombstone.
        await _messageRepository.UnpinAsync(channelId, messageId, ct);

        if (guildId is { } gid)
        {
            await _auditLog.LogAsync(
                gid,
                userId,
                AuditLogAction.MessageUnpin,
                targetId: messageId,
                changes: new { channelId },
                ct: ct
            );
        }

        await _broadcaster.BroadcastMessageUnpinnedAsync(new MessagePinPayload(messageId, channelId), ct);
    }

    public async Task<IReadOnlyList<PinnedMessageResponse>> GetPinsAsync(
        long userId,
        long? guildId,
        long channelId,
        CancellationToken ct = default
    )
    {
        // Read authorization mirrors GetChannelMessagesAsync: guild → ViewChannel + ReadHistory
        // (overrides apply); DM → participant.
        if (guildId is { } gid)
        {
            var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, gid);
            if (channel is null)
                throw new KeyNotFoundException("Channel not found.");

            const long readMask = (long)(Permission.ViewChannel | Permission.ReadHistory);
            var bits = await _permissions.ResolveAsync(userId, gid, channelId, ct);
            if ((bits & readMask) != readMask)
                throw new UnauthorizedAccessException(
                    "You do not have permission to view this channel."
                );
        }
        else
        {
            await GetDmChannelOrThrowAsync(channelId);
            if (!await _dms.IsParticipantAsync(channelId, userId))
                throw new UnauthorizedAccessException(
                    "You are not a participant of this conversation."
                );
        }

        var pins = (await _messageRepository.GetPinnedAsync(channelId, ct)).ToList();
        if (pins.Count == 0)
            return [];

        // Resolve each pinned message from messages_by_id; skip any since hard-gone or soft-deleted.
        var results = new List<(PinnedMessage Pin, Message Message)>(pins.Count);
        foreach (var pin in pins)
        {
            var message = await _messageRepository.GetByIdAsync(pin.MessageId, ct);
            if (message is null || message.IsDeleted)
                continue;
            results.Add((pin, message));
        }

        var users = await _userRepository.GetByIdsAsync(results.Select(r => r.Message.UserId).Distinct());

        // GetPinnedAsync already returns pinned_at DESC (most-recently-pinned first) — preserve it.
        return results
            .Select(r => new PinnedMessageResponse(
                Message: MapMessage(r.Message, guildId, users),
                PinnedBy: r.Pin.PinnedBy,
                PinnedAt: r.Pin.PinnedAt
            ))
            .ToList();
    }

    /// <summary>
    /// Authorizes a pin/unpin: guild → <see cref="Permission.PinMessages"/> (channel-scoped, so
    /// overrides apply; owners/administrators resolve to all bits); DM/group → the caller must be a
    /// participant (no moderators in a DM). Throws 404/403 as appropriate.
    /// </summary>
    private async Task AuthorizePinActionAsync(
        long userId,
        long? guildId,
        long channelId,
        CancellationToken ct
    )
    {
        if (guildId is { } gid)
        {
            var channel = await _channelRepository.GetByIdAndGuildIdAsync(channelId, gid);
            if (channel is null)
                throw new KeyNotFoundException("Channel not found.");
            if (!await _permissions.HasAsync(userId, gid, Permission.PinMessages, channelId, ct))
                throw new UnauthorizedAccessException(
                    "You do not have permission to pin messages in this channel."
                );
        }
        else
        {
            await GetDmChannelOrThrowAsync(channelId);
            if (!await _dms.IsParticipantAsync(channelId, userId))
                throw new UnauthorizedAccessException(
                    "You are not a participant of this conversation."
                );
        }
    }

    // -------------------------------------------------------------------------
    // DM authorization helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts the channel exists and is a DM or group DM (no owning guild). Throws 404 otherwise,
    /// so a guild channel id can never be driven down the guild-less DM path. Returns the channel
    /// so callers can branch on the DM type without a second lookup.
    /// </summary>
    private async Task<Channel> GetDmChannelOrThrowAsync(long channelId)
    {
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (
            channel is null
            || channel.GuildId is not null
            || (channel.Type != "dm" && channel.Type != "group_dm")
        )
            throw new KeyNotFoundException("Channel not found.");
        return channel;
    }

    /// <summary>
    /// Authorizes a DM send: the channel must be a DM, and the caller a participant. For a 1:1
    /// DM a pairwise block hard-stops the send in either direction; a group DM is soft — a block
    /// only hides content client-side, so two members who blocked each other can coexist in a group.
    /// </summary>
    private async Task AuthorizeDmSendAsync(long userId, long channelId)
    {
        var channel = await GetDmChannelOrThrowAsync(channelId);

        var participantIds = await _dms.GetParticipantIdsAsync(channelId);
        if (!participantIds.Contains(userId))
            throw new UnauthorizedAccessException("You are not a participant of this conversation.");

        if (channel.Type == "dm")
        {
            // Blocking suppresses 1:1 DMs in either direction.
            foreach (var peerId in participantIds)
            {
                if (peerId != userId && await _blocks.AreBlockedAsync(userId, peerId))
                    throw new UnauthorizedAccessException("You cannot send messages to this user.");
            }
        }
    }
}
