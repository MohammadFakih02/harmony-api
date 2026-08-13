using Harmony.Domain.Interfaces;
using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// Per-channel grain (key = channelId). It owns the <em>broadcast</em> of message mutations in its
/// channel: a single activation, single-threaded, so concurrent operations on the same channel
/// serialize through it — a natural per-channel order with no locks. The grain does not persist; it
/// fans out immediately (broadcast-first) and hands the async work off to RabbitMQ. D2a added
/// <see cref="SendMessage"/>; D2b added <see cref="EditMessage"/> + <see cref="DeleteMessage"/>.
/// </summary>
public interface IChannelGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Broadcast-first send: fan the authoritative message out to the channel's SignalR subscribers
    /// <em>now</em>, then republish the event to RabbitMQ (flagged <see cref="MessageSentEvent.BroadcastAlready"/>)
    /// as the durable persist log. The event is already validated/authorized by
    /// <c>MessageService</c>; the grain only renders + delivers it.
    /// </summary>
    Task SendMessage(MessageSentEvent evt);

    /// <summary>
    /// Broadcast-first edit (D2b): the edited content is already written to Scylla synchronously by
    /// <c>MessageService</c> before this call, so there is no durability window — the grain fans the
    /// edit out to subscribers <em>now</em>, then republishes the event (flagged
    /// <see cref="MessageEditedEvent.BroadcastAlready"/>) so the consumer does its remaining async work
    /// (newly-added-mention notifications) without re-broadcasting.
    /// </summary>
    Task EditMessage(MessageEditedEvent evt);

    /// <summary>
    /// Broadcast-first delete (D2b): the soft-delete is already written to Scylla synchronously by
    /// <c>MessageService</c>, so the grain fans the deletion out <em>now</em>, then republishes the
    /// event (flagged <see cref="MessageDeletedEvent.BroadcastAlready"/>) for the consumer's async
    /// work without re-broadcasting.
    /// </summary>
    Task DeleteMessage(MessageDeletedEvent evt);
}
