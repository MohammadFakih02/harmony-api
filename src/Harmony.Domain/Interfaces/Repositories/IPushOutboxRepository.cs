using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IPushOutboxRepository
{
    /// <summary>
    /// Stages an outbox row WITHOUT saving — the caller's own SaveChanges commits it,
    /// which is what makes the row atomic with the Notification row it mirrors.
    /// </summary>
    Task AddAsync(PushOutboxMessage message);

    /// <summary>
    /// Rows due for dispatch (next_attempt_at &lt;= now), oldest first. Tracked — the
    /// dispatcher mutates attempts/backoff or removes each row after processing.
    /// </summary>
    Task<List<PushOutboxMessage>> GetDueAsync(long nowMs, int limit);

    void Remove(PushOutboxMessage message);

    Task SaveChangesAsync();
}
