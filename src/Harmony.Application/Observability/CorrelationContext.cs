namespace Harmony.Application.Observability;

/// <summary>
/// The ambient per-request correlation id, flowed via <see cref="AsyncLocal{T}"/> so it is readable
/// anywhere in the same async call chain — notably the RabbitMQ publisher, deep in Infrastructure —
/// without threading a parameter through every method signature.
/// </summary>
/// <remarks>
/// Set once at the API edge by <c>CorrelationIdMiddleware</c>. Outside a request (e.g. a background
/// service that publishes a message) it is <c>null</c>, and the publisher falls back to a fresh id so
/// every message still carries something a consumer can group its own logs by. This is logging/tracing
/// context only — it never influences control flow, persistence, or authorization.
/// </remarks>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Value = new();

    /// <summary>Header the id is read from (if a gateway/client supplied one) and echoed back on.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Structured-log property name used everywhere this id is surfaced.</summary>
    public const string LogProperty = "CorrelationId";

    public static string? Current
    {
        get => Value.Value;
        set => Value.Value = value;
    }
}
