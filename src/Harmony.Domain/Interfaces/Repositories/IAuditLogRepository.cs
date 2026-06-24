using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog auditLog);

    Task SaveChangesAsync();

    /// <summary>
    /// A most-recent-first page of a guild's audit entries. <paramref name="before"/> is a
    /// keyset cursor — only rows with a smaller id (i.e. older, since snowflake ids are
    /// time-ordered) are returned; null starts from the newest. <paramref name="actionType"/>
    /// filters to a single action when supplied.
    /// </summary>
    Task<List<AuditLog>> GetForGuildAsync(
        long guildId,
        int limit,
        long? before = null,
        string? actionType = null
    );
}
