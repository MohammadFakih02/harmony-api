using Harmony.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Harmony.Infrastructure.Resilience;

public class ResilientMessagePublisher : IMessagePublisher
{
    private readonly IMessagePublisher _inner;
    private readonly ILogger<ResilientMessagePublisher> _logger;
    private readonly Polly.AsyncPolicy _policy;

    public ResilientMessagePublisher(
        IMessagePublisher inner,
        ILogger<ResilientMessagePublisher> logger
    )
    {
        _inner = inner;
        _logger = logger;
        _policy = ResiliencePolicies.RabbitMQCircuitBreaker(logger);
    }

    public async Task PublishMessageSentAsync(MessageSentEvent evt, CancellationToken ct = default)
    {
        try
        {
            await _policy.ExecuteAsync(() => _inner.PublishMessageSentAsync(evt, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "RabbitMQ circuit is OPEN — MessageSent event dropped for MessageId: {MessageId}",
                evt.MessageId
            );
            throw new InvalidOperationException(
                "Messaging service is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task PublishMessageDeletedAsync(
        MessageDeletedEvent evt,
        CancellationToken ct = default
    )
    {
        try
        {
            await _policy.ExecuteAsync(() => _inner.PublishMessageDeletedAsync(evt, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "RabbitMQ circuit is OPEN — MessageDeleted event dropped for MessageId: {MessageId}",
                evt.MessageId
            );
            throw new InvalidOperationException(
                "Messaging service is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task PublishMessageEditedAsync(
        MessageEditedEvent evt,
        CancellationToken ct = default
    )
    {
        try
        {
            await _policy.ExecuteAsync(() => _inner.PublishMessageEditedAsync(evt, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "RabbitMQ circuit is OPEN — MessageEdited event dropped for MessageId: {MessageId}",
                evt.MessageId
            );
            throw new InvalidOperationException(
                "Messaging service is temporarily unavailable. Please try again shortly."
            );
        }
    }
}
