using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Entities;

namespace Harmony.Application.Hubs;

/// <summary>
/// Strongly-typed contract for all server → client SignalR push events.
///
/// Lives in Harmony.Application so both Harmony.API (ChatHub) and
/// Harmony.Infrastructure (ScyllaMessageConsumer broadcast) can reference it
/// without creating a circular dependency.
/// </summary>
public interface IChatClient
{
    /// <summary>
    /// Fired after the RabbitMQ consumer confirms ScyllaDB persistence.
    /// Sent to everyone in the channel group — including the original sender.
    /// The sender should replace its optimistic local message with this authoritative copy.
    /// </summary>
    Task MessageReceived(MessageResponse message);

    /// <summary>
    /// Fired after a message is soft-deleted and confirmed in ScyllaDB.
    /// Clients redact the message content locally on receipt.
    /// </summary>
    Task MessageDeleted(MessageDeletedPayload payload);

    /// <summary>
    /// Fired after a message edit is confirmed in ScyllaDB.
    /// Clients patch the existing message content locally on receipt.
    /// </summary>
    Task MessageEdited(MessageEditedPayload payload);

    /// <summary>
    /// Fired when a message is pinned in a channel. Broadcast to the channel group so any
    /// connected member updates their pinned-message indicator and refreshes the pins panel.
    /// </summary>
    Task MessagePinned(MessagePinPayload payload);

    /// <summary>
    /// Fired when a message is unpinned (manually, or because it was deleted). Broadcast to the
    /// channel group so clients drop it from the pins panel / clear the pinned indicator.
    /// </summary>
    Task MessageUnpinned(MessagePinPayload payload);

    /// <summary>
    /// Fired when channel metadata changes (create / update / reorder).
    /// Clients refresh their channel list on receipt.
    /// Distinct from ChannelDeleted — clients should update, not remove.
    /// </summary>
    Task ChannelUpdated(ChannelResponse channel);

    /// <summary>
    /// Fired when a channel is permanently deleted.
    /// Clients must remove the channel from the sidebar and navigate away
    /// if the user is currently viewing it.
    /// Distinct from ChannelUpdated — clients must remove, not update.
    /// </summary>
    Task ChannelDeleted(ChannelDeletedPayload payload);

    /// <summary>
    /// Fired when a user's unread count for a channel changes (new message they
    /// didn't send, or a mark-as-read reset to zero). Sent per-user via
    /// Clients.User — reaches all of that user's connections for multi-device sync.
    /// Carries the absolute count, not a delta, so the client is self-correcting.
    /// </summary>
    Task UnreadCountUpdated(UnreadCountPayload payload);

    /// <summary>
    /// Fired when the consumer's Scylla persist fails after all retries are exhausted.
    /// Sent only to the original sender via Clients.User — other users never saw an
    /// optimistic copy and must not receive this event.
    /// The client should remove the optimistic message and show an error state.
    /// </summary>
    Task MessageFailed(MessageFailedPayload payload);

    /// <summary>
    /// Fired when a user's first connection comes online. Sent to that user's friends
    /// via Clients.User (per-recipient) — recipient resolution is a seam that returns
    /// no one until the friends-system feature lands.
    /// </summary>
    Task OnlineStatus(OnlineStatusPayload payload);

    /// <summary>
    /// Fired when a user's last connection drops (all tabs/devices closed). Same
    /// per-recipient delivery and friends-system seam as OnlineStatus.
    /// </summary>
    Task OfflineStatus(OfflineStatusPayload payload);

    /// <summary>
    /// Fired when a connected user's status changes (manual status pick or the
    /// 15-min idle toggle). Friends receive the public effective status; the user's
    /// own connections receive their raw preferred status (so other tabs sync the
    /// real choice — including invisible/dnd, which friends never see as such).
    /// </summary>
    Task StatusChanged(StatusChangedPayload payload);

    /// <summary>
    /// Fired when one of the user's mutes ends — either swept by MuteExpiryService
    /// once its expiry passes, or removed by a manual unmute. Sent only to the mute's
    /// owner via Clients.User so all their tabs drop the mute from local state.
    /// </summary>
    Task MuteExpired(MuteExpiredPayload payload);

    /// <summary>
    /// Fired when a user sends the recipient a friend request. Sent to the addressee
    /// via Clients.User so all their tabs surface the incoming request. Carries the
    /// requester's public identity so the client can render it without a refetch.
    /// </summary>
    Task FriendRequest(FriendUserPayload payload);

    /// <summary>
    /// Fired when a pending request is accepted. Sent to <em>both</em> parties via
    /// Clients.User; the payload identifies the other user who is now a friend.
    /// </summary>
    Task FriendAccepted(FriendUserPayload payload);

    /// <summary>
    /// Fired when a friendship/pending request is removed — declined, cancelled,
    /// unfriended, or dropped because one user blocked the other. Sent to the other
    /// party via Clients.User with just the id so they prune local friend state.
    /// </summary>
    Task FriendRemoved(FriendRemovedPayload payload);

    /// <summary>
    /// Fired when a notification (mention, friend request, ...) is created for the
    /// user. Sent only to the owner via Clients.User. The persisted Notification row
    /// is the source of truth — this event is a live push on top of it for whichever
    /// of the owner's tabs are connected; an offline owner picks the row up on next
    /// load via GET /api/notifications instead.
    /// </summary>
    Task NotificationReceived(NotificationPayload payload);

    /// <summary>
    /// Fired when a member is removed from a guild (kicked, banned, or left). Broadcast to the
    /// guild group so every connected member prunes them from the member list.
    /// </summary>
    Task MemberRemoved(MemberRemovedPayload payload);

    /// <summary>
    /// Fired to the affected user when they are kicked or banned. Sent via Clients.User so all
    /// their tabs drop the guild from local state and navigate away. <see cref="KickedPayload.Banned"/>
    /// distinguishes a ban (cannot rejoin) from a kick.
    /// </summary>
    Task Kicked(KickedPayload payload);

    /// <summary>
    /// Fired when a member's guild-level state changes (timeout set/cleared, or server nickname
    /// changed). Broadcast to the guild group so clients update the timed-out indicator, the
    /// affected user's own composer, and the member's displayed name everywhere in the guild.
    /// </summary>
    Task MemberUpdated(MemberUpdatedPayload payload);

    /// <summary>Fired when a role is created. Broadcast to the guild group so clients add it to the role list.</summary>
    Task RoleCreated(RoleResponse role);

    /// <summary>Fired when a role's fields change (perms/name/color/position/...). Broadcast to the guild group.</summary>
    Task RoleUpdated(RoleResponse role);

    /// <summary>Fired when a role is deleted. Broadcast to the guild group so clients drop it.</summary>
    Task RoleDeleted(RoleDeletedPayload payload);

    /// <summary>Fired when a member's role assignments change. Broadcast to the guild group; carries the
    /// member's full current role-id set so clients re-render role-derived UI (colors, badges).</summary>
    Task MemberRoleUpdated(MemberRoleUpdatedPayload payload);

    /// <summary>
    /// Fired when a DM/group channel's membership changes (group created, a participant added,
    /// or someone left). Sent via Clients.Users to every current participant — and the just-added
    /// or just-departed user — so each refetches their DM list (GET /api/dm) and re-joins the
    /// channel group. A coarse "something changed, resync" signal, not a delta.
    /// </summary>
    Task DmChannelUpdated(DmChannelUpdatedPayload payload);
}

/// <summary>Minimal delete notification — no content, just identity. GuildId is null for DMs.</summary>
public record MessageDeletedPayload(
    long MessageId,
    long ChannelId,
    long? GuildId,
    long DeletedByUserId,
    long DeletedAt
);

/// <summary>Minimal edit notification — new content and metadata only. GuildId is null for DMs.</summary>
public record MessageEditedPayload(
    long MessageId,
    long ChannelId,
    long? GuildId,
    long EditedByUserId,
    string NewContent,
    long EditedAt
);

/// <summary>Minimal pin/unpin notification — message + channel identity. Reused for both events.</summary>
public record MessagePinPayload(long MessageId, long ChannelId);

/// <summary>
/// Channel deletion notification sent to all guild group subscribers.
/// Carries enough context for the client to navigate away if needed.
/// </summary>
public record ChannelDeletedPayload(long ChannelId, long GuildId, long DeletedAt);

/// <summary>Absolute unread count for one user in one channel. GuildId is null for DMs.</summary>
public record UnreadCountPayload(long ChannelId, long? GuildId, int UnreadCount);

/// <summary>Failure notification sent to the original sender of an undeliverable message. GuildId is null for DMs.</summary>
public record MessageFailedPayload(long MessageId, long ChannelId, long? GuildId);

/// <summary>A user came online (their first connection was established).</summary>
public record OnlineStatusPayload(long UserId, string Status);

/// <summary>A user went offline (their last connection dropped).</summary>
public record OfflineStatusPayload(long UserId);

/// <summary>
/// A connected user's status changed. <see cref="Status"/> carries either the public
/// effective status (to friends) or the raw preferred status (to the user's own tabs),
/// depending on the audience the broadcaster targeted.
/// </summary>
public record StatusChangedPayload(long UserId, string Status, string? StatusMessage);

/// <summary>A mute the user held has ended (expired or manually removed).</summary>
public record MuteExpiredPayload(long TargetId, string TargetType);

/// <summary>
/// Public identity of the user a friend event concerns — the requester (FriendRequest)
/// or the now-confirmed friend (FriendAccepted). No email or other private fields.
/// </summary>
public record FriendUserPayload(
    long Id,
    string Username,
    string? AvatarKey,
    string? BannerKey
);

/// <summary>A friendship or pending request involving the user was removed.</summary>
public record FriendRemovedPayload(long UserId);

/// <summary>
/// A notification created for the user (e.g. Type="mention" or "friend_request").
/// GuildId/ChannelId/MessageId are null when the notification type has no such
/// context (a friend request has none of the three).
/// </summary>
public record NotificationPayload(
    long Id,
    string Type,
    long ActorId,
    long? GuildId,
    long? ChannelId,
    long? MessageId,
    long CreatedAt
);

/// <summary>A member was removed from a guild (kick / ban / leave). Sent to the guild group.</summary>
public record MemberRemovedPayload(long GuildId, long UserId);

/// <summary>Sent to the user who was kicked or banned. <see cref="Banned"/> = true means a ban.</summary>
public record KickedPayload(long GuildId, string? Reason, bool Banned);

/// <summary>A member's guild-level state changed (timeout set/cleared, or nickname changed). Carries
/// the member's full mutable state so applying it never clobbers an unrelated field:
/// <see cref="Nickname"/> is the server nickname (null = none/reverted to username);
/// <see cref="CommunicationDisabledUntil"/> is the unix-ms timeout expiry (null = no active timeout).</summary>
public record MemberUpdatedPayload(
    long GuildId,
    long UserId,
    string? Nickname,
    long? CommunicationDisabledUntil
);

/// <summary>A role was deleted from a guild.</summary>
public record RoleDeletedPayload(long GuildId, long RoleId);

/// <summary>A member's role-id set after an assignment change.</summary>
public record MemberRoleUpdatedPayload(long GuildId, long UserId, IEnumerable<long> RoleIds);

/// <summary>A DM/group channel whose membership changed — the recipient should resync its DM list.</summary>
public record DmChannelUpdatedPayload(long ChannelId);
