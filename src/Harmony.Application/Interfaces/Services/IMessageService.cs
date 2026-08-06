using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Services;

public interface IMessageService
{
    Task<SendMessageResponse> SendMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        SendMessageRequest request,
        CancellationToken ct = default,
        // Server-built forward snapshot (internal — ForwardMessageAsync sets it). A non-null
        // snapshot is itself content, so it satisfies the "content or attachment" requirement.
        MessageForwardSnapshot? forward = null
    );

    /// <summary>
    /// Forwards a message into <paramref name="channelId"/>: reads the original, verifies the
    /// forwarder can actually see it (no permission/DM leak), and stamps a server-authoritative
    /// attributed-quote snapshot. Any <c>AttachmentIds</c> and note travel like a normal send.
    /// </summary>
    Task<SendMessageResponse> ForwardMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        ForwardMessageRequest request,
        CancellationToken ct = default
    );

    Task<ChannelMessagesResponse> GetChannelMessagesAsync(
        long userId,
        long? guildId,
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        long? aroundMessageId = null,
        long? afterMessageId = null,
        CancellationToken ct = default
    );

    Task DeleteMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    );

    Task EditMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        EditMessageRequest request,
        CancellationToken ct = default
    );

    /// <summary>
    /// Pins a message in a channel. Guild → requires <c>PinMessages</c>; DM/group → any participant.
    /// Idempotent, capped at 50/channel. Posts a system notice + audit in a guild; broadcasts
    /// <c>MessagePinned</c> to the channel group.
    /// </summary>
    Task PinMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Unpins a message. Same authorization as <see cref="PinMessageAsync"/>; idempotent. Audits in a
    /// guild; broadcasts <c>MessageUnpinned</c>. No system notice.
    /// </summary>
    Task UnpinMessageAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Lists a channel's pinned messages (most-recently-pinned first), resolved to full
    /// <see cref="PinnedMessageResponse"/>s. Read authorization mirrors channel history (guild →
    /// ViewChannel + ReadHistory; DM → participant). Skips deleted messages.
    /// </summary>
    Task<IReadOnlyList<PinnedMessageResponse>> GetPinsAsync(
        long userId,
        long? guildId,
        long channelId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Adds an emoji reaction to a message. Guild → requires <c>ViewChannel</c> + <c>AddReactions</c>
    /// (channel-scoped, overrides apply); DM/group → any participant. Idempotent (re-reacting is a
    /// no-op). Broadcasts <c>ReactionAdded</c> to the channel group. <paramref name="emoji"/> is a
    /// single Unicode grapheme in v1.
    /// </summary>
    Task AddReactionAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        string emoji,
        CancellationToken ct = default
    );

    /// <summary>
    /// Removes the caller's own reaction. Same authorization as <see cref="AddReactionAsync"/>;
    /// idempotent. Broadcasts <c>ReactionRemoved</c>.
    /// </summary>
    Task RemoveReactionAsync(
        long userId,
        long? guildId,
        long channelId,
        long messageId,
        string emoji,
        CancellationToken ct = default
    );

    /// <summary>
    /// Publishes a server-generated system message (e.g. a member-join welcome notice or a
    /// group-DM join/leave notice) into any channel — guild channel or guild-less DM
    /// (<paramref name="guildId"/> null). Bypasses the permission/timeout/attachment/content
    /// gates of a normal send — the caller (a controller/service) is responsible for
    /// authorization. The message flows through the same RabbitMQ → Scylla → broadcast pipeline;
    /// <paramref name="authorUserId"/> is the subject of the notice (e.g. the joining user) so
    /// the client can render it. Mentions are not parsed for system messages. Returns the
    /// minted message id.
    /// </summary>
    Task<long> PublishSystemMessageAsync(
        long? guildId,
        long channelId,
        long authorUserId,
        string messageType,
        string content,
        CancellationToken ct = default
    );
}
