using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Microsoft.AspNetCore.SignalR;

namespace Harmony.API.Hubs;

/// <summary>
/// Concrete implementation of IHubBroadcaster.
///
/// Lives in Harmony.API because it holds IHubContext{ChatHub, IChatClient} —
/// the only layer where ChatHub is known. Registered as a singleton in DI.
///
/// Infrastructure injects IHubBroadcaster (the abstraction) and never
/// touches ChatHub or any SignalR type directly.
/// </summary>
public class HubBroadcaster : IHubBroadcaster
{
    private readonly IHubContext<ChatHub, IChatClient> _hubContext;

    public HubBroadcaster(IHubContext<ChatHub, IChatClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task BroadcastMessageReceivedAsync(
        MessageResponse message,
        CancellationToken ct = default
    ) =>
        _hubContext.Clients.Group(ChatHub.ChannelGroup(message.ChannelId)).MessageReceived(message);

    public Task BroadcastMessageDeletedAsync(
        MessageDeletedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(payload.ChannelId)).MessageDeleted(payload);

    public Task BroadcastMessageEditedAsync(
        MessageEditedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(payload.ChannelId)).MessageEdited(payload);

    public Task BroadcastMessagePinnedAsync(
        MessagePinPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(payload.ChannelId)).MessagePinned(payload);

    public Task BroadcastMessageUnpinnedAsync(
        MessagePinPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(payload.ChannelId)).MessageUnpinned(payload);

    public Task BroadcastReactionAddedAsync(
        ReactionPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(payload.ChannelId)).ReactionAdded(payload);

    public Task BroadcastReactionRemovedAsync(
        ReactionPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(payload.ChannelId)).ReactionRemoved(payload);

    public Task BroadcastTypingStartedAsync(
        long channelId,
        long userId,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(channelId)).TypingStarted(userId, channelId);

    public Task BroadcastTypingStoppedAsync(
        long channelId,
        long userId,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.ChannelGroup(channelId)).TypingStopped(userId, channelId);

    public Task BroadcastChannelUpdatedAsync(
        ChannelResponse channel,
        long guildId,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).ChannelUpdated(channel);

    public Task BroadcastChannelDeletedAsync(
        long channelId,
        long guildId,
        CancellationToken ct = default
    ) =>
        _hubContext
            .Clients.Group(ChatHub.GuildGroup(guildId))
            .ChannelDeleted(
                new ChannelDeletedPayload(
                    ChannelId: channelId,
                    GuildId: guildId,
                    DeletedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                )
            );

    public Task BroadcastUnreadCountAsync(
        long userId,
        UnreadCountPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(userId.ToString()).UnreadCountUpdated(payload);

    public Task BroadcastMessageFailedAsync(
        long senderId,
        MessageFailedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(senderId.ToString()).MessageFailed(payload);

    public Task BroadcastOnlineStatusAsync(
        long recipientId,
        OnlineStatusPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).OnlineStatus(payload);

    public Task BroadcastOfflineStatusAsync(
        long recipientId,
        OfflineStatusPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).OfflineStatus(payload);

    public Task BroadcastStatusChangedAsync(
        long recipientId,
        StatusChangedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).StatusChanged(payload);

    public Task BroadcastOnlineStatusToGuildAsync(
        long guildId,
        OnlineStatusPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).OnlineStatus(payload);

    public Task BroadcastOfflineStatusToGuildAsync(
        long guildId,
        OfflineStatusPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).OfflineStatus(payload);

    public Task BroadcastStatusChangedToGuildAsync(
        long guildId,
        StatusChangedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).StatusChanged(payload);

    public Task BroadcastMuteExpiredAsync(
        long userId,
        MuteExpiredPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(userId.ToString()).MuteExpired(payload);

    public Task BroadcastFriendRequestAsync(
        long recipientId,
        FriendUserPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).FriendRequest(payload);

    public Task BroadcastFriendAcceptedAsync(
        long recipientId,
        FriendUserPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).FriendAccepted(payload);

    public Task BroadcastFriendRemovedAsync(
        long recipientId,
        FriendRemovedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).FriendRemoved(payload);

    public Task BroadcastNotificationReceivedAsync(
        long userId,
        NotificationPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(userId.ToString()).NotificationReceived(payload);

    public Task BroadcastNotificationBadgeAsync(
        long userId,
        int unreadCount,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(userId.ToString()).NotificationBadgeUpdate(unreadCount);

    public Task BroadcastMemberRemovedAsync(
        long guildId,
        MemberRemovedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).MemberRemoved(payload);

    public Task BroadcastKickedAsync(
        long userId,
        KickedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(userId.ToString()).Kicked(payload);

    public Task BroadcastMemberUpdatedAsync(
        long guildId,
        MemberUpdatedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).MemberUpdated(payload);

    public Task BroadcastMemberJoinedAsync(
        long guildId,
        MemberJoinedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).MemberJoined(payload);

    public Task BroadcastRoleCreatedAsync(
        long guildId,
        RoleResponse role,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).RoleCreated(role);

    public Task BroadcastRoleUpdatedAsync(
        long guildId,
        RoleResponse role,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).RoleUpdated(role);

    public Task BroadcastRoleDeletedAsync(
        long guildId,
        RoleDeletedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).RoleDeleted(payload);

    public Task BroadcastMemberRoleUpdatedAsync(
        long guildId,
        MemberRoleUpdatedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).MemberRoleUpdated(payload);

    public Task BroadcastDmChannelUpdatedAsync(
        IReadOnlyList<long> userIds,
        DmChannelUpdatedPayload payload,
        CancellationToken ct = default
    ) =>
        _hubContext
            .Clients.Users(userIds.Select(id => id.ToString()).ToList())
            .DmChannelUpdated(payload);

    public Task BroadcastProfileUpdatedToGuildAsync(
        long guildId,
        ProfileUpdatedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.Group(ChatHub.GuildGroup(guildId)).ProfileUpdated(payload);

    public Task BroadcastProfileUpdatedToUserAsync(
        long userId,
        ProfileUpdatedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(userId.ToString()).ProfileUpdated(payload);

    public Task BroadcastGuildInvitesChangedAsync(long guildId, CancellationToken ct = default) =>
        _hubContext
            .Clients.Group(ChatHub.GuildGroup(guildId))
            .GuildInvitesChanged(new GuildInvitesChangedPayload(guildId));

    public async Task BroadcastVoiceParticipantJoinedAsync(
        VoiceParticipantPayload payload,
        CancellationToken ct = default
    )
    {
        await _hubContext
            .Clients.Group(ChatHub.ChannelGroup(payload.ChannelId))
            .VoiceParticipantJoined(payload);
        if (payload.GuildId is { } guildId)
            await _hubContext
                .Clients.Group(ChatHub.GuildGroup(guildId))
                .VoiceParticipantJoined(payload);
    }

    public async Task BroadcastVoiceParticipantLeftAsync(
        VoiceParticipantLeftPayload payload,
        CancellationToken ct = default
    )
    {
        await _hubContext
            .Clients.Group(ChatHub.ChannelGroup(payload.ChannelId))
            .VoiceParticipantLeft(payload);
        if (payload.GuildId is { } guildId)
            await _hubContext
                .Clients.Group(ChatHub.GuildGroup(guildId))
                .VoiceParticipantLeft(payload);
    }

    public async Task BroadcastVoiceStateUpdatedAsync(
        VoiceParticipantPayload payload,
        CancellationToken ct = default
    )
    {
        await _hubContext
            .Clients.Group(ChatHub.ChannelGroup(payload.ChannelId))
            .VoiceStateUpdated(payload);
        if (payload.GuildId is { } guildId)
            await _hubContext
                .Clients.Group(ChatHub.GuildGroup(guildId))
                .VoiceStateUpdated(payload);
    }

    public Task BroadcastIncomingCallAsync(
        IReadOnlyList<long> userIds,
        IncomingCallPayload payload,
        CancellationToken ct = default
    ) =>
        _hubContext
            .Clients.Users(userIds.Select(id => id.ToString()).ToList())
            .IncomingCall(payload);

    public Task BroadcastCallCancelledAsync(
        IReadOnlyList<long> userIds,
        CallCancelledPayload payload,
        CancellationToken ct = default
    ) =>
        _hubContext
            .Clients.Users(userIds.Select(id => id.ToString()).ToList())
            .CallCancelled(payload);

    public Task BroadcastCallDeclinedAsync(
        long recipientId,
        CallDeclinedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).CallDeclined(payload);

    public Task BroadcastVoiceForceMovedAsync(
        long recipientId,
        VoiceForceMovedPayload payload,
        CancellationToken ct = default
    ) => _hubContext.Clients.User(recipientId.ToString()).VoiceForceMoved(payload);
}
