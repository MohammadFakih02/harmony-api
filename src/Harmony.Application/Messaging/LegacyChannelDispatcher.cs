using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces;

namespace Harmony.Application.Messaging;

/// <summary>
/// The pre-D2a <see cref="IChannelDispatcher"/>: publish the <see cref="MessageSentEvent"/> straight
/// to RabbitMQ and let the <c>ScyllaMessageConsumer</c> broadcast it after persistence. This is the
/// path used in the Test environment (where Orleans is gated out, so there is no ChannelGrain) and
/// it is also the unit-test default for <c>MessageService</c>, keeping the send-path
/// publisher assertions meaningful. Behaviour is byte-identical to the direct
/// <c>_publisher.PublishMessageSentAsync</c> call it replaced — the event's
/// <see cref="MessageSentEvent.BroadcastAlready"/> stays false, so the consumer broadcasts.
/// </summary>
public sealed class LegacyChannelDispatcher : IChannelDispatcher
{
    private readonly IMessagePublisher _publisher;

    public LegacyChannelDispatcher(IMessagePublisher publisher) => _publisher = publisher;

    public Task DispatchSentAsync(MessageSentEvent evt, CancellationToken ct = default) =>
        _publisher.PublishMessageSentAsync(evt, ct);

    public Task DispatchEditedAsync(MessageEditedEvent evt, CancellationToken ct = default) =>
        _publisher.PublishMessageEditedAsync(evt, ct);

    public Task DispatchDeletedAsync(MessageDeletedEvent evt, CancellationToken ct = default) =>
        _publisher.PublishMessageDeletedAsync(evt, ct);
}
