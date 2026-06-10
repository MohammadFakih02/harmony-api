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
}
