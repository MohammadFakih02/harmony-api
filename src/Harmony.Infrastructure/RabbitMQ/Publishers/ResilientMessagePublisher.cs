using Harmony.Domain.Interfaces;
using Polly;

namespace Harmony.Infrastructure.RabbitMQ.Producers;

/// <summary>
/// Decorator that wraps every RabbitMQ publish in a stateful circuit-breaker pipeline.
/// When the circuit opens, <see cref="Polly.CircuitBreaker.BrokenCircuitException"/> propagates
/// to the caller; GlobalExceptionHandler maps it to HTTP 503.
/// The <see cref="ResiliencePipeline"/> MUST be a singleton; a per-scope pipeline never trips.
/// </summary>
public sealed class ResilientMessagePublisher : IMessagePublisher
{
    private readonly IMessagePublisher _inner;
    private readonly ResiliencePipeline _pipeline;

    public ResilientMessagePublisher(IMessagePublisher inner, ResiliencePipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public async Task PublishMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default) =>
        await _pipeline.ExecuteAsync(
            async token => await _inner.PublishMessageSentAsync(evt, token),
            ct
        );

    public async Task PublishMessageDeletedAsync(
        MessageDeletedEvent evt,
        CancellationToken ct = default
    ) =>
        await _pipeline.ExecuteAsync(
            async token => await _inner.PublishMessageDeletedAsync(evt, token),
            ct
        );

    public async Task PublishMessageEditedAsync(
        MessageEditedEvent evt,
        CancellationToken ct = default
    ) =>
        await _pipeline.ExecuteAsync(
            async token => await _inner.PublishMessageEditedAsync(evt, token),
            ct
        );

    public async Task PublishChannelDeletedAsync(
        ChannelDeletedEvent evt,
        CancellationToken ct = default
    ) =>
        await _pipeline.ExecuteAsync(
            async token => await _inner.PublishChannelDeletedAsync(evt, token),
            ct
        );
}
