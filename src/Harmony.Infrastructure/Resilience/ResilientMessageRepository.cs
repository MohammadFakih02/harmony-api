using Harmony.Core.Domain.Entities;
using Harmony.Core.Exceptions;
using Harmony.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Harmony.Infrastructure.Resilience;

public class ResilientMessageRepository : IMessageRepository
{
    private readonly IMessageRepository _inner;
    private readonly ILogger<ResilientMessageRepository> _logger;
    private readonly Polly.IAsyncPolicy _policy;

    public ResilientMessageRepository(
        IMessageRepository inner,
        ScyllaPolicyProvider policyProvider,
        ILogger<ResilientMessageRepository> logger
    )
    {
        _inner = inner;
        _logger = logger;
        _policy = policyProvider.Policy;
    }

    public async Task<IEnumerable<Message>> GetChannelMessagesAsync(
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    )
    {
        try
        {
            return await _policy.ExecuteAsync(() =>
                _inner.GetChannelMessagesAsync(channelId, limit, beforeMessageId, ct)
            );
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — GetChannelMessages failed for ChannelId: {ChannelId}",
                channelId
            );
            throw new ServiceUnavailableException(
                "Message history is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task<Message?> GetByIdAsync(long messageId, CancellationToken ct = default)
    {
        try
        {
            return await _policy.ExecuteAsync(() => _inner.GetByIdAsync(messageId, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — GetById failed for MessageId: {MessageId}",
                messageId
            );
            throw new ServiceUnavailableException(
                "Message lookup is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task SaveAsync(Message message, CancellationToken ct = default)
    {
        try
        {
            await _policy.ExecuteAsync(() => _inner.SaveAsync(message, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — SaveAsync failed for MessageId: {MessageId}",
                message.MessageId
            );
            throw new ServiceUnavailableException(
                "Message persistence is temporarily unavailable."
            );
        }
    }

    public async Task DeleteAsync(long messageId, long channelId, CancellationToken ct = default)
    {
        try
        {
            await _policy.ExecuteAsync(() => _inner.DeleteAsync(messageId, channelId, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — DeleteAsync failed for MessageId: {MessageId}",
                messageId
            );
            throw new ServiceUnavailableException(
                "Message deletion is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task EditAsync(
        long messageId,
        long channelId,
        string newContent,
        CancellationToken ct = default
    )
    {
        try
        {
            await _policy.ExecuteAsync(() =>
                _inner.EditAsync(messageId, channelId, newContent, ct)
            );
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — EditAsync failed for MessageId: {MessageId}",
                messageId
            );
            throw new ServiceUnavailableException(
                "Message editing is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task PinAsync(
        long channelId,
        long messageId,
        long pinnedBy,
        CancellationToken ct = default
    )
    {
        try
        {
            await _policy.ExecuteAsync(() => _inner.PinAsync(channelId, messageId, pinnedBy, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — PinAsync failed for MessageId: {MessageId}",
                messageId
            );
            throw new ServiceUnavailableException(
                "Message pinning is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task UnpinAsync(long channelId, long pinnedAt, CancellationToken ct = default)
    {
        try
        {
            await _policy.ExecuteAsync(() => _inner.UnpinAsync(channelId, pinnedAt, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — UnpinAsync failed for ChannelId: {ChannelId}",
                channelId
            );
            throw new ServiceUnavailableException(
                "Message unpinning is temporarily unavailable. Please try again shortly."
            );
        }
    }

    public async Task<IEnumerable<PinnedMessage>> GetPinnedAsync(
        long channelId,
        CancellationToken ct = default
    )
    {
        try
        {
            return await _policy.ExecuteAsync(() => _inner.GetPinnedAsync(channelId, ct));
        }
        catch (BrokenCircuitException)
        {
            _logger.LogError(
                "ScyllaDB circuit is OPEN — GetPinnedAsync failed for ChannelId: {ChannelId}",
                channelId
            );
            throw new ServiceUnavailableException(
                "Pinned messages are temporarily unavailable. Please try again shortly."
            );
        }
    }
}
