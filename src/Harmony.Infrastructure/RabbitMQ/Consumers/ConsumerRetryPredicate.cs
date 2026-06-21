using Npgsql;

namespace Harmony.Infrastructure.RabbitMQ.Consumers;

/// <summary>
/// Shared retry predicates for the RabbitMQ message consumers. Both consumers retry
/// transient failures on an exponential ladder before routing to the DLQ. This
/// distinguishes deterministic <i>poison</i> — a payload that will fail identically on
/// every redelivery — so it DLQs immediately instead of burning the full 2s/4s/8s ladder
/// (which, on a serial consumer, stalls the queue head for healthy traffic behind it).
/// </summary>
public static class ConsumerRetryPredicate
{
    /// <summary>
    /// True if the exception — or any inner exception, since EF wraps the driver error in
    /// <c>DbUpdateException</c> — is a PostgreSQL integrity-constraint violation
    /// (SQLSTATE class 23: 23502 not-null, 23503 foreign-key, 23505 unique, 23514 check,
    /// 23P01 exclusion). These are deterministic poison.
    ///
    /// Transient failures are intentionally NOT matched and must still retry: connection
    /// errors (class 08), deadlock_detected (40P01), serialization_failure (40001) and
    /// cannot_connect_now (57P03).
    /// </summary>
    public static bool IsConstraintViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (
                current is PostgresException pg
                && pg.SqlState is { } state
                && state.StartsWith("23", StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }
}
