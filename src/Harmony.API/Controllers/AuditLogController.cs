using Harmony.API.Filters;
using Harmony.Application.DTOs.Responses;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Read-only view of a guild's moderation history. Gated by <see cref="Permission.ViewAuditLog"/>
/// via <see cref="RequirePermissionAttribute"/> (the guild owner resolves to all bits, so they
/// always pass; <c>guildId</c> in the route satisfies the filter). Entries are written through
/// <c>IAuditLogService</c> by the guild-management producers — this controller only reads.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId}/audit-log")]
[Authorize]
[EnableRateLimiting("api")]
public class AuditLogController : ControllerBase
{
    private const int MaxLimit = 100;

    private readonly IAuditLogRepository _auditLogs;
    private readonly IUserRepository _users;

    public AuditLogController(IAuditLogRepository auditLogs, IUserRepository users)
    {
        _auditLogs = auditLogs;
        _users = users;
    }

    // GET /api/guilds/{guildId}/audit-log?limit=50&before=&action=
    [HttpGet]
    [RequirePermission(Permission.ViewAuditLog)]
    public async Task<IActionResult> GetAuditLog(
        long guildId,
        int limit = 50,
        long? before = null,
        string? action = null,
        CancellationToken ct = default
    )
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        var entries = await _auditLogs.GetForGuildAsync(guildId, take, before, action);

        var actors = await _users.GetByIdsAsync(entries.Select(e => e.ActorId).Distinct());

        return Ok(
            entries.Select(e =>
            {
                actors.TryGetValue(e.ActorId, out var actor);
                return new AuditLogEntryResponse(
                    e.Id,
                    e.ActorId,
                    actor?.UserName,
                    actor?.AvatarKey,
                    e.ActionType,
                    e.TargetId,
                    e.Changes,
                    e.Reason,
                    e.CreatedAt
                );
            })
        );
    }
}
