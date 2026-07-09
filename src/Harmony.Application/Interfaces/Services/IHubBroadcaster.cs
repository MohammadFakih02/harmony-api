using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Abstracts SignalR hub broadcasts so Harmony.Infrastructure can push
/// real-time events without depending on Harmony.API or any SignalR types.
///
/// The concrete implementation (HubBroadcaster) lives in Harmony.API and
/// holds the real IHubContext{ChatHub, IChatClient}.
/// Registered as a singleton in DI.
/// </summary>
public interface IHubBroadcaster
{
    /// <summary>
    /// Broadcasts a persisted message to all connections subscribed to the channel group.
    /// Called by ScyllaMessageConsumer after ScyllaDB write is confirmed.
    /// </summary>
    Task BroadcastMessageReceivedAsync(MessageResponse message, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts a soft-delete event to all connections subscribed to the channel group.
    /// </summary>
    Task BroadcastMessageDeletedAsync(
        MessageDeletedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Broadcasts a message edit event to all connections subscribed to the channel group.
    /// </summary>
    Task BroadcastMessageEditedAsync(MessageEditedPayload payload, CancellationToken ct = default);

    /// <summary>Broadcasts a pin event to all connections subscribed to the channel group.</summary>
    Task BroadcastMessagePinnedAsync(MessagePinPayload payload, CancellationToken ct = default);

    /// <summary>Broadcasts an unpin event to all connections subscribed to the channel group.</summary>
    Task BroadcastMessageUnpinnedAsync(MessagePinPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts an ephemeral typing-started signal to the channel group. The typer's own client
    /// filters itself out. Invoked from the hub (StartTyping) — routing it through the broadcaster
    /// keeps every SignalR broadcast flowing through this one chokepoint.
    /// </summary>
    Task BroadcastTypingStartedAsync(long channelId, long userId, CancellationToken ct = default);

    /// <summary>Broadcasts an ephemeral typing-stopped signal to the channel group.</summary>
    Task BroadcastTypingStoppedAsync(long channelId, long userId, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts a channel metadata change (create / update / reorder) to all
    /// connections in the guild group. Clients update their channel list.
    /// </summary>
    Task BroadcastChannelUpdatedAsync(
        ChannelResponse channel,
        long guildId,
        CancellationToken ct = default
    );

    /// <summary>
    /// Broadcasts a channel deletion to all connections in the guild group.
    /// Clients remove the channel from the sidebar and navigate away if viewing it.
    /// </summary>
    Task BroadcastChannelDeletedAsync(long channelId, long guildId, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts an unread-count update to a single user (all their connections).
    /// Per-user, not group — unread counts are personal.
    /// </summary>
    Task BroadcastUnreadCountAsync(
        long userId,
        UnreadCountPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies the original sender that their message failed to persist after all retries.
    /// Per-sender only (Clients.User) — other users never saw an optimistic copy.
    /// </summary>
    Task BroadcastMessageFailedAsync(
        long senderId,
        MessageFailedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies a single recipient that a user came online. Per-recipient
    /// (Clients.User) — callers fan this out over a resolved recipient list.
    /// </summary>
    Task BroadcastOnlineStatusAsync(
        long recipientId,
        OnlineStatusPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies a single recipient that a user went offline. Same per-recipient
    /// fan-out shape as BroadcastOnlineStatusAsync.
    /// </summary>
    Task BroadcastOfflineStatusAsync(
        long recipientId,
        OfflineStatusPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies a single recipient of a user's status change. Same per-recipient
    /// shape as the online/offline broadcasters; the caller chooses the payload's
    /// Status per audience (effective for friends, preferred for the user itself).
    /// </summary>
    Task BroadcastStatusChangedAsync(
        long recipientId,
        StatusChangedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Fans a presence update out to an entire guild's group (every connected member who has joined
    /// it) — so co-members see each other come online / change status live, not only friends. Masking
    /// (invisible → suppressed/offline) is applied by the caller before this is invoked.
    /// </summary>
    Task BroadcastOnlineStatusToGuildAsync(
        long guildId,
        OnlineStatusPayload payload,
        CancellationToken ct = default
    );

    Task BroadcastOfflineStatusToGuildAsync(
        long guildId,
        OfflineStatusPayload payload,
        CancellationToken ct = default
    );

    Task BroadcastStatusChangedToGuildAsync(
        long guildId,
        StatusChangedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies a single user that one of their mutes ended (expired or manually
    /// removed). Per-user (Clients.User) — a mute is private to its owner.
    /// </summary>
    Task BroadcastMuteExpiredAsync(
        long userId,
        MuteExpiredPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies the addressee of an incoming friend request. Per-user (Clients.User).
    /// </summary>
    Task BroadcastFriendRequestAsync(
        long recipientId,
        FriendUserPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies a single party that a friend request was accepted. Per-user
    /// (Clients.User) — the caller invokes this once per party with the other
    /// user's identity as the payload.
    /// </summary>
    Task BroadcastFriendAcceptedAsync(
        long recipientId,
        FriendUserPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies a single user that a friendship/pending request was removed. Per-user
    /// (Clients.User).
    /// </summary>
    Task BroadcastFriendRemovedAsync(
        long recipientId,
        FriendRemovedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies a single user that a notification was created for them (mention,
    /// friend request, ...). Per-user (Clients.User) — a notification is private
    /// to its owner.
    /// </summary>
    Task BroadcastNotificationReceivedAsync(
        long userId,
        NotificationPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Broadcasts a member removal (kick / ban / leave) to the guild group so every connected
    /// member updates their member list.
    /// </summary>
    Task BroadcastMemberRemovedAsync(
        long guildId,
        MemberRemovedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies the affected user (all their tabs) that they were kicked or banned from a guild.
    /// </summary>
    Task BroadcastKickedAsync(long userId, KickedPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts a member moderation-state change (timeout set/cleared) to the guild group.
    /// </summary>
    Task BroadcastMemberUpdatedAsync(
        long guildId,
        MemberUpdatedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Broadcasts a member join (invite redeem) to the guild group so every connected member adds
    /// them to their member list live.
    /// </summary>
    Task BroadcastMemberJoinedAsync(
        long guildId,
        MemberJoinedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>Broadcasts a role create/update to the guild group.</summary>
    Task BroadcastRoleCreatedAsync(long guildId, RoleResponse role, CancellationToken ct = default);

    Task BroadcastRoleUpdatedAsync(long guildId, RoleResponse role, CancellationToken ct = default);

    /// <summary>Broadcasts a role deletion to the guild group.</summary>
    Task BroadcastRoleDeletedAsync(
        long guildId,
        RoleDeletedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>Broadcasts a member's role-assignment change to the guild group.</summary>
    Task BroadcastMemberRoleUpdatedAsync(
        long guildId,
        MemberRoleUpdatedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Notifies the given users (all their tabs) that a DM/group channel's membership changed,
    /// so each resyncs its DM list. Sent via Clients.Users to the explicit recipient set.
    /// </summary>
    Task BroadcastDmChannelUpdatedAsync(
        IReadOnlyList<long> userIds,
        DmChannelUpdatedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Broadcasts a profile-avatar change to an entire guild's group (every connected member who
    /// has joined it) — so co-members' member lists and chat authors update live.
    /// </summary>
    Task BroadcastProfileUpdatedToGuildAsync(
        long guildId,
        ProfileUpdatedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Sends a profile-avatar change to a single user (all their tabs) — used for the user's own
    /// connections and each of their friends, so DM lists / friends lists / the user deck update live.
    /// </summary>
    Task BroadcastProfileUpdatedToUserAsync(
        long userId,
        ProfileUpdatedPayload payload,
        CancellationToken ct = default
    );

    /// <summary>
    /// Broadcasts a coarse "this guild's invites changed" signal (create / revoke / redeem) to the
    /// guild group. Carries no invite data — clients with invite UI open refetch the list through
    /// the permission-enforcing GET.
    /// </summary>
    Task BroadcastGuildInvitesChangedAsync(long guildId, CancellationToken ct = default);

    /// <summary>
    /// Broadcasts a voice-participant join to the channel group and (when the payload carries a
    /// guildId) the guild group, so both the in-call roster and the sidebar voice-channel roster
    /// update live. Invoked by the voice-state service after Redis state is written.
    /// </summary>
    Task BroadcastVoiceParticipantJoinedAsync(
        VoiceParticipantPayload payload,
        CancellationToken ct = default
    );

    /// <summary>Broadcasts a voice-participant leave to the channel group and (if guild) the guild group.</summary>
    Task BroadcastVoiceParticipantLeftAsync(
        VoiceParticipantLeftPayload payload,
        CancellationToken ct = default
    );

    /// <summary>Broadcasts a voice-participant state change (mute/deafen/video/screenshare) to the channel + guild groups.</summary>
    Task BroadcastVoiceStateUpdatedAsync(
        VoiceParticipantPayload payload,
        CancellationToken ct = default
    );
}
