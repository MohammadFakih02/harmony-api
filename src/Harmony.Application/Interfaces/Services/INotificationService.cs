using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Owns the decide-and-act lifecycle for a notification: filters through the
/// recipient's <see cref="NotificationPreference"/>, blocks (IUserBlockRepository),
/// and mutes (IUserMuteRepository), then — if not suppressed — persists the
/// Notification row and pushes it live via IHubBroadcaster.NotificationReceived.
/// Callers don't get a yes/no back because they have nothing to branch on either
/// way: notification creation is a best-effort side effect of the action that
/// triggered it (sending a mention, sending a friend request), never something
/// the caller's own response depends on.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Creates (and pushes) a "mention" notification for each mentioned user who
    /// isn't suppressed — self-mention, disabled MentionsEnabled preference,
    /// blocked-with-the-sender, or muted (the sender as a user, the channel, or
    /// the guild) all skip that recipient without an error.
    /// </summary>
    Task CreateMentionNotificationsAsync(
        List<long> mentionedUserIds,
        long actorId,
        long? guildId,
        long channelId,
        long messageId,
        long createdAt,
        IReadOnlyCollection<long>? everyoneOriginIds = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// The per-message ("all" level) producer: creates a <c>"message"</c> notification for each user
    /// who opted this guild/channel into the <c>all</c> level — minus the sender and anyone in
    /// <paramref name="alreadyNotifiedIds"/> (mention/reply recipients for this same message, to avoid
    /// a double ping) — subject to the usual mute/block/DnD suppression chain. Guild messages only;
    /// no-op when <paramref name="guildId"/> is null or nobody opted in.
    /// </summary>
    Task CreateMessageNotificationsAsync(
        long actorId,
        long guildId,
        long channelId,
        long messageId,
        long createdAt,
        IReadOnlyCollection<long> alreadyNotifiedIds,
        CancellationToken ct = default
    );

    /// <summary>
    /// Creates (and pushes) a "guild_invite" notification for a user a friend invited to a server,
    /// unless suppressed by their GuildInvites preference, a mute on the inviter, or a block between
    /// the two. Fired by the server-side invite-a-friend flow (never client-triggered).
    /// </summary>
    Task CreateGuildInviteNotificationAsync(
        long recipientId,
        long actorId,
        long guildId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Creates (and pushes) a "friend_request" notification for the addressee,
    /// unless suppressed by their FriendRequests preference, a mute on the
    /// requester, or a block between the two. FriendsController already rejects
    /// the request itself in the blocked case, so that guard is normally a no-op
    /// for this caller — kept anyway so the service is correct on its own if ever
    /// called from elsewhere.
    /// </summary>
    Task CreateFriendRequestNotificationAsync(
        long addresseeId,
        long requesterId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Creates (and pushes) a "reply" notification for the author of the message being
    /// replied to, unless suppressed — self-reply, disabled RepliesEnabled preference,
    /// a "nothing" notification level on the channel/guild, a mute (the sender as a user,
    /// the channel, or the guild), or a block between the two. The caller is expected to
    /// skip authors already covered by a mention notification for the same message.
    /// </summary>
    Task CreateReplyNotificationAsync(
        long recipientId,
        long actorId,
        long? guildId,
        long channelId,
        long messageId,
        long createdAt,
        CancellationToken ct = default
    );
}
