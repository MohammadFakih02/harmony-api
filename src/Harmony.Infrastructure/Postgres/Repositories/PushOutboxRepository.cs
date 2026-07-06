using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class PushOutboxRepository : IPushOutboxRepository
{
    private readonly HarmonyDbContext _db;

    public PushOutboxRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(PushOutboxMessage message) => await _db.PushOutbox.AddAsync(message);

    // Tracked: the dispatcher removes or bumps attempts on each row after processing.
    public async Task<List<PushOutboxMessage>> GetDueAsync(long nowMs, int limit) =>
        await _db
            .PushOutbox.Where(m => m.NextAttemptAt <= nowMs)
            .OrderBy(m => m.Id)
            .Take(limit)
            .ToListAsync();

    public void Remove(PushOutboxMessage message) => _db.PushOutbox.Remove(message);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
