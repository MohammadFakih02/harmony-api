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
}
