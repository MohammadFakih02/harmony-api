using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Application.Messaging;

/// <summary>
/// The two steps shared by the two places that broadcast a brand-new message's authoritative
/// <see cref="MessageResponse"/>: the <c>ScyllaMessageConsumer</c> (legacy/Test path) and the D2a
/// per-channel <c>ChannelGrain</c> (broadcast-first prod path). Kept here, in one place, so those
/// two paths can never drift on the sender-display lookup or the response shape.
/// </summary>
public static class MessageSendFanout
{
    /// <summary>
    /// Resolves the sender's render fields (username + avatar) through the read-through
    /// <see cref="IUserDisplayCache"/> — a Redis GET on the hot path, with a repository fallback that
    /// repopulates the cache on a miss. Best-effort: any failure (Redis down, repo error) degrades to
    /// the <c>("Unknown", null)</c> placeholder rather than throwing, exactly as the consumer did
    /// inline before this was extracted. A not-yet-existing user's placeholder is never cached.
    /// </summary>
    public static async Task<UserDisplay> ResolveSenderDisplayAsync(
        IUserDisplayCache cache,
        IUserRepository users,
        long userId,
        ILogger logger,
        CancellationToken ct = default
    )
    {
        try
        {
            var display = await cache.GetAsync(userId, ct);
            if (display is null)
            {
                var sender = await users.GetByIdAsync(userId);
                display = new UserDisplay(sender?.UserName ?? "Unknown", sender?.AvatarKey);
                if (sender is not null)
                    await cache.SetAsync(userId, display.Value, ct);
            }
            return display.Value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve sender display for {UserId}", userId);
            return new UserDisplay("Unknown", null);
        }
    }

    /// <summary>
    /// Builds the authoritative <see cref="MessageResponse"/> broadcast for a brand-new message from
    /// its <see cref="MessageSentEvent"/> and the resolved sender display. A new message has no
    /// reactions yet (they arrive via ReactionAdded), is neither deleted nor edited, and echoes the
    /// sender's optimistic-send nonce so their client can reconcile the bubble in place.
    /// </summary>
    public static MessageResponse BuildReceived(MessageSentEvent evt, UserDisplay display) =>
        new(
            MessageId: evt.MessageId,
            ChannelId: evt.ChannelId,
            GuildId: evt.GuildId,
            UserId: evt.UserId,
            Username: display.Username,
            AvatarKey: display.AvatarKey,
            Content: evt.Content,
            MessageType: evt.MessageType,
            IsDeleted: false,
            IsEdited: false,
            ReplyToId: evt.ReplyToId,
            MentionIds: evt.MentionIds,
            AttachmentIds: evt.AttachmentIds,
            SentAt: evt.SentAt.ToUnixTimeMilliseconds(),
            EditedAt: null,
            Reactions: [],
            Forward: evt.Forward is null
                ? null
                : new ForwardSnapshotResponse(
                    evt.Forward.AuthorId,
                    evt.Forward.AuthorName,
                    evt.Forward.Content,
                    evt.Forward.SentAt
                ),
            Nonce: evt.Nonce
        );
}
