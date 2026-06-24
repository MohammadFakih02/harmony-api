using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly HarmonyDbContext _db;

    public AuditLogRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(AuditLog auditLog) => await _db.AuditLogs.AddAsync(auditLog);

    public async Task<List<AuditLog>> GetForGuildAsync(
        long guildId,
        int limit,
        long? before = null,
        string? actionType = null
    )
    {
        var query = _db.AuditLogs.Where(a => a.GuildId == guildId);

        // Keyset pagination: snowflake ids are time-ordered, so "older than the cursor" is id < before.
        if (before is { } cursor)
            query = query.Where(a => a.Id < cursor);

        if (!string.IsNullOrWhiteSpace(actionType))
            query = query.Where(a => a.ActionType == actionType);

        return await query.OrderByDescending(a => a.Id).Take(limit).ToListAsync();
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
