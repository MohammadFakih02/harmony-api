using Cassandra;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Polly;

namespace Harmony.Infrastructure.Scylla.Repositories;

/// <summary>
/// Decorator that wraps Scylla read operations in a stateful circuit-breaker pipeline.
/// Writes delegate straight through — the consumer's retry policy owns write resilience.
/// The <see cref="ResiliencePipeline"/> MUST be a singleton; a per-scope pipeline never trips.
/// </summary>
public sealed class ResilientMessageRepository : IMessageRepository
{
    private readonly IMessageRepository _inner;
    private readonly ResiliencePipeline _pipeline;

    public ResilientMessageRepository(IMessageRepository inner, ResiliencePipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    // ── Reads — protected by circuit breaker ─────────────────────────────────

    public async Task<IEnumerable<Message>> GetChannelMessagesAsync(
        long channelId,
        int limit = 50,
        long? beforeMessageId = null,
        CancellationToken ct = default
    ) =>
        await _pipeline.ExecuteAsync(
            async token => await _inner.GetChannelMessagesAsync(channelId, limit, beforeMessageId, token),
            ct
        );

    public async Task<Message?> GetByIdAsync(long messageId, CancellationToken ct = default) =>
        await _pipeline.ExecuteAsync(
            async token => await _inner.GetByIdAsync(messageId, token),
            ct
        );

    public async Task<IEnumerable<PinnedMessage>> GetPinnedAsync(
        long channelId,
        CancellationToken ct = default
    ) =>
        await _pipeline.ExecuteAsync(
            async token => await _inner.GetPinnedAsync(channelId, token),
            ct
        );

    // ── Writes — pass through; consumer retry policy owns write resilience ───

    public Task SaveAsync(Message message, CancellationToken ct = default) =>
        _inner.SaveAsync(message, ct);

    public Task DeleteAsync(long messageId, long channelId, CancellationToken ct = default) =>
        _inner.DeleteAsync(messageId, channelId, ct);

    public Task EditAsync(
        long messageId,
        long channelId,
        string newContent,
        CancellationToken ct = default
    ) => _inner.EditAsync(messageId, channelId, newContent, ct);

    public Task PinAsync(
        long channelId,
        long messageId,
        long pinnedBy,
        CancellationToken ct = default
    ) => _inner.PinAsync(channelId, messageId, pinnedBy, ct);

    public Task UnpinAsync(long channelId, long pinnedAt, CancellationToken ct = default) =>
        _inner.UnpinAsync(channelId, pinnedAt, ct);

    public Task PurgeChannelPartitionsAsync(long channelId, CancellationToken ct = default) =>
        _inner.PurgeChannelPartitionsAsync(channelId, ct);
}
