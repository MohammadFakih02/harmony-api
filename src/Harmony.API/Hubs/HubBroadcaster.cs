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
}
