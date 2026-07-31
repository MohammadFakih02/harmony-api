namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// The single write seam for guild audit entries. Producers (the guild-management trio and
/// the moderator message-delete path) call <see cref="LogAsync"/>; it mints the snowflake id,
/// serializes the <c>changes</c> object into the jsonb column, and persists the row
/// <b>best-effort</b> — a failed audit write is logged and swallowed so it can never fail the
/// moderation action that triggered it (same fail-open side-effect philosophy as the
/// notification fan-out).
/// </summary>
public interface IAuditLogService
{
    Task LogAsync(
        long guildId,
        long actorId,
        string actionType,
        long? targetId = null,
        object? changes = null,
        string? reason = null,
        CancellationToken ct = default
    );
}
