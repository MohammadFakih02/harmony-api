using System.Text.Json;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// EF-backed implementation of <see cref="IAuditLogService"/>. Mints the snowflake, serializes
/// the optional <c>changes</c> object to the jsonb column, and writes the row best-effort: any
/// failure is logged and swallowed so the moderation action that triggered the audit entry is
/// never failed by the audit write itself (mirrors the notification fan-out's fail-open posture).
/// </summary>
public class AuditLogService : IAuditLogService
{
    private readonly IAuditLogRepository _auditLogs;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        IAuditLogRepository auditLogs,
        ISnowflakeIdGenerator snowflake,
        ILogger<AuditLogService> logger
    )
    {
        _auditLogs = auditLogs;
        _snowflake = snowflake;
        _logger = logger;
    }

    public async Task LogAsync(
        long guildId,
        long actorId,
        string actionType,
        long? targetId = null,
        object? changes = null,
        string? reason = null,
        CancellationToken ct = default
    )
    {
        try
        {
            var entry = new AuditLog
            {
                Id = _snowflake.NextId(),
                GuildId = guildId,
                ActorId = actorId,
                ActionType = actionType,
                TargetId = targetId,
                Changes = changes is null ? null : JsonSerializer.Serialize(changes),
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            await _auditLogs.AddAsync(entry);
            await _auditLogs.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to write audit log entry: guild={GuildId} actor={ActorId} action={ActionType}",
                guildId,
                actorId,
                actionType
            );
        }
    }
}
