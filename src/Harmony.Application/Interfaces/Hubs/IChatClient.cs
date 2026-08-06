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
    /// Ephemeral: a user started typing in a channel. Broadcast to the whole channel group (the
    /// typer's own client filters itself out). No persistence — the client resolves the display name
    /// from its own stores (nickname-aware) and auto-expires the indicator if no further signal
    /// arrives. Paired with <see cref="TypingStopped"/> (sent on send).
    /// </summary>
    Task TypingStarted(long userId, long channelId);

    /// <summary>Ephemeral: a user stopped typing in a channel (e.g. sent the message).</summary>
    Task TypingStopped(long userId, long channelId);

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
    /// Pushes the owner's current unread-notification count so their bell badge can update without
    /// a refetch. Fired on both create and read/clear (Clients.User). Purely a UI convenience — the
    /// authoritative count is always GET /api/notifications/unread-count, so a missed push self-heals.
    /// </summary>
    Task NotificationBadgeUpdate(int unreadCount);

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

    /// <summary>
    /// Fired when a member joins a guild (invite redeem). Broadcast to the guild group so every
    /// connected member adds them to the member list live — without this a rejoining/new member only
    /// appears after a manual refresh.
    /// </summary>
    Task MemberJoined(MemberJoinedPayload payload);

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

    /// <summary>
    /// Fired when a user changes their profile avatar (uploaded or removed) or renames their
    /// username. Fanned out to every guild the user is a member of (guild groups) plus their
    /// friends and their own tabs — the surfaces that render these live (member lists, chat
    /// authors, DM list, the user deck). Each field is independent: an avatar-only update carries
    /// <c>Username: null</c> and vice versa — the client patches only the fields it receives.
    /// </summary>
    Task ProfileUpdated(ProfileUpdatedPayload payload);

    /// <summary>
    /// Fired when a guild's invite set changes (created, revoked, or redeemed — a use-count bump).
    /// Broadcast to the guild group as a coarse "resync" signal, like <see cref="DmChannelUpdated"/>:
    /// it deliberately carries no invite data, so codes never reach members who can't list them —
    /// a client with the invite modal open refetches through the permission-enforcing GET instead.
    /// </summary>
    Task GuildInvitesChanged(GuildInvitesChangedPayload payload);

    /// <summary>
    /// Fired when a user joins a voice channel/DM call. Broadcast to the channel group AND (for a
    /// guild voice channel) the guild group — so the voice roster updates live both for people in
    /// the call and for members watching the sidebar. Voice state is ephemeral (Redis), so a
    /// client that missed the event catches up via GET .../voice/participants on load.
    /// </summary>
    Task VoiceParticipantJoined(VoiceParticipantPayload payload);

    /// <summary>Fired when a user leaves a voice channel/DM call (manually, disconnect, or ghost sweep).</summary>
    Task VoiceParticipantLeft(VoiceParticipantLeftPayload payload);

    /// <summary>Fired when a participant's voice flags change (mute/deafen/video/screenshare toggles,
    /// or a moderator setting/clearing a server mute/deafen).</summary>
    Task VoiceStateUpdated(VoiceParticipantPayload payload);

    /// <summary>
    /// Fired to a user a moderator moved to another voice channel (targeted via Clients.Users —
    /// their Redis state has already moved). The client reconnects media to the new channel with a
    /// fresh token; a client that ignores it is cut from the old room server-side anyway.
    /// </summary>
    Task VoiceForceMoved(VoiceForceMovedPayload payload);

    /// <summary>
    /// Fired when a DM/group-DM call starts ringing. Sent via Clients.Users to every participant
    /// except the caller (who is already in the room) — the ring is user-targeted, not a channel
    /// broadcast, because callees haven't joined anything yet. Ephemeral: a missed ring simply
    /// expires (the offline arm is a web push staged through the PushOutbox instead).
    /// </summary>
    Task IncomingCall(IncomingCallPayload payload);

    /// <summary>
    /// Fired when a ring ends unanswered — the caller cancelled/timed out, or (to a decliner's own
    /// other tabs) the user declined elsewhere. Recipients dismiss the incoming-call UI.
    /// </summary>
    Task CallCancelled(CallCancelledPayload payload);

    /// <summary>
    /// Fired to the caller when a callee declines the ring. In a 1:1 DM the caller's client ends
    /// the call; in a group DM it is informational (others keep ringing).
    /// </summary>
    Task CallDeclined(CallDeclinedPayload payload);

    /// <summary>
    /// Fired when a user adds an emoji reaction to a message. Broadcast to the channel group; the
    /// client applies a delta to that emoji's pill (++count, and sets "me" when the actor is itself),
    /// so no message refetch is needed. A message outside the loaded window is ignored.
    /// </summary>
    Task ReactionAdded(ReactionPayload payload);

    /// <summary>Fired when a user removes their reaction. Broadcast to the channel group; the client
    /// applies the inverse delta (--count, drops the pill at zero).</summary>
    Task ReactionRemoved(ReactionPayload payload);

    /// <summary>
    /// Fired when a channel's permission overrides change (upsert or delete). Broadcast to the
    /// guild group as a coarse "resync" signal: an override can grant or revoke any per-channel
    /// capability (including ViewChannel), so clients re-resolve the guild's channel list and the
    /// open channel's capabilities through the permission-enforcing GETs.
    /// </summary>
    Task ChannelOverridesChanged(ChannelOverridesChangedPayload payload);
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

/// <summary>
/// A user came online (their first connection was established). StatusMessage rides along so
/// observers see the custom status immediately — the broadcast is already suppressed for
/// invisible users, so every state this fires for is message-visible (same masking outcome as
/// StatusChanged's "null when observer-sees-offline").
/// </summary>
public record OnlineStatusPayload(long UserId, string Status, string? StatusMessage);

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

/// <summary>A member joined a guild — carries the full member record so clients can insert it into
/// the member list without a follow-up fetch.</summary>
public record MemberJoinedPayload(long GuildId, GuildMemberResponse Member);

/// <summary>A role was deleted from a guild.</summary>
public record RoleDeletedPayload(long GuildId, long RoleId);

/// <summary>A member's role-id set after an assignment change.</summary>
public record MemberRoleUpdatedPayload(long GuildId, long UserId, IEnumerable<long> RoleIds);

/// <summary>A DM/group channel whose membership changed — the recipient should resync its DM list.</summary>
public record DmChannelUpdatedPayload(long ChannelId);

/// <summary>A user's avatar and/or username changed. <see cref="AvatarKey"/> is always the user's
/// CURRENT storage key (null = genuinely no avatar) — every producer includes it, even a
/// username-only change, so a client that applies it unconditionally never mistakes "didn't
/// touch the avatar" for "avatar removed". <see cref="Username"/> is the new username, or null
/// when this update didn't touch it (a client applies it only when non-null).</summary>
public record ProfileUpdatedPayload(long UserId, string? AvatarKey, string? Username);

/// <summary>A guild's invite set changed — clients with invite UI open should refetch the list.</summary>
public record GuildInvitesChangedPayload(long GuildId);

/// <summary>
/// A participant's current state in a voice room. Carries the full flag set so applying a join or a
/// state-change never needs a follow-up fetch. <see cref="GuildId"/> is null for a DM/group-DM call.
/// Server flags (<see cref="IsServerMuted"/>/<see cref="IsServerDeafened"/>) are moderator-imposed
/// and orthogonal to the self-reported flags — only a moderator clears them.
/// </summary>
public record VoiceParticipantPayload(
    long ChannelId,
    long? GuildId,
    long UserId,
    bool IsMuted,
    bool IsDeafened,
    bool IsVideoOn,
    bool IsStreaming,
    bool IsServerMuted,
    bool IsServerDeafened,
    long JoinedAt
);

/// <summary>A participant left a voice room. Identity only — clients drop them from the roster.</summary>
public record VoiceParticipantLeftPayload(long ChannelId, long? GuildId, long UserId);

/// <summary>You were moved to another voice channel by a moderator — reconnect media there.</summary>
public record VoiceForceMovedPayload(long FromChannelId, long ToChannelId, long? GuildId);

/// <summary>A DM/group-DM call is ringing. <see cref="StartedAt"/> is unix-ms.</summary>
public record IncomingCallPayload(long ChannelId, long CallerId, long StartedAt);

/// <summary>A ring ended unanswered (caller cancelled/timed out, or you declined on another tab).</summary>
public record CallCancelledPayload(long ChannelId);

/// <summary>A callee declined the ring; sent to the caller. <see cref="UserId"/> is the decliner.</summary>
public record CallDeclinedPayload(long ChannelId, long UserId);

/// <summary>
/// A reaction was added/removed on a message. Carries just the identity + the emoji token; the client
/// recomputes the pill count/highlight locally (it knows its own id). <see cref="GuildId"/> is null
/// for a DM. Reused for both ReactionAdded and ReactionRemoved.
/// </summary>
public record ReactionPayload(long MessageId, long ChannelId, long? GuildId, string Emoji, long UserId);

/// <summary>A channel's permission overrides changed — clients resync channel list + capabilities.</summary>
public record ChannelOverridesChangedPayload(long GuildId, long ChannelId);
