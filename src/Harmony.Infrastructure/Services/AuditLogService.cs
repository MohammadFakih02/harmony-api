using System.Text.Json;
using System.Text.Json.Serialization;
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

    // Serialize snowflake longs inside the `changes` blob AS STRINGS, upholding the codebase-wide
    // "every long is a string on the wire" invariant. A raw 18-digit id in the stringified jsonb
    // would otherwise be corrupted by the frontend's bigInt interceptor (it quotes bare 16+-digit
    // runs in value position) → JSON.parse throws → the audit log fails to load.
    private static readonly JsonSerializerOptions ChangesJsonOptions = new()
    {
        Converters = { new LongToStringConverter(), new NullableLongToStringConverter() },
    };

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
                Changes = changes is null
                    ? null
                    : JsonSerializer.Serialize(changes, ChangesJsonOptions),
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

    /// <summary>Writes a long as a JSON string (reads either form back), matching the wire invariant.</summary>
    private sealed class LongToStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? long.Parse(reader.GetString()!)
                : reader.GetInt64();

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class NullableLongToStringConverter : JsonConverter<long?>
    {
        public override long? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.String => long.Parse(reader.GetString()!),
                _ => reader.GetInt64(),
            };

        public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
        {
            if (value is null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value.Value.ToString());
        }
    }
}
