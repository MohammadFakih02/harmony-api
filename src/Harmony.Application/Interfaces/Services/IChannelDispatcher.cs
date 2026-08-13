using Harmony.Domain.Interfaces;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// D2a strangler seam for the message-send fan-out (mirrors <see cref="IPresenceService"/> for
/// presence). <c>MessageService</c> hands a validated, snowflake-stamped
/// <see cref="MessageSentEvent"/> to this seam instead of publishing to RabbitMQ directly, so the
/// broadcast strategy is swapped by DI, not by touching the service:
/// <list type="bullet">
///   <item><b>Legacy / Test</b> (<c>LegacyChannelDispatcher</c>): publish the event to RabbitMQ; the
///   persist consumer broadcasts after it lands — the pre-D2a behaviour, byte-identical.</item>
///   <item><b>Prod / dev</b> (<c>GrainChannelDispatcher</c>): route to the per-channel
///   <c>ChannelGrain</c>, which fans the message out to SignalR <em>immediately</em> (broadcast-first)
///   and then republishes the event as the durable persist log.</item>
/// </list>
/// </summary>
public interface IChannelDispatcher
{
    /// <summary>
    /// Dispatches a validated, already-authorized new message for broadcast + async persistence. The
    /// event is authoritative — the seam neither re-validates nor mutates it (beyond the grain path
    /// setting <see cref="MessageSentEvent.BroadcastAlready"/> on the copy it republishes).
    /// </summary>
    Task DispatchSentAsync(MessageSentEvent evt, CancellationToken ct = default);

    /// <summary>
    /// D2b: dispatches an edit (already written to Scylla synchronously) for broadcast + the
    /// consumer's async follow-up. Grain path broadcasts first; legacy path publishes so the consumer
    /// broadcasts.
    /// </summary>
    Task DispatchEditedAsync(MessageEditedEvent evt, CancellationToken ct = default);

    /// <summary>
    /// D2b: dispatches a delete (already soft-deleted in Scylla synchronously) for broadcast + the
    /// consumer's async follow-up. Grain path broadcasts first; legacy path publishes so the consumer
    /// broadcasts.
    /// </summary>
    Task DispatchDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default);
}
