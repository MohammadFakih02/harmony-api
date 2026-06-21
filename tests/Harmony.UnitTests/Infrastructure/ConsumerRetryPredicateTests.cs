using FluentAssertions;
using Harmony.Infrastructure.RabbitMQ.Consumers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harmony.UnitTests.Infrastructure;

/// <summary>
/// The poison-message fast-fail predicate: PostgreSQL integrity-constraint violations
/// (SQLSTATE class 23) are deterministic poison and must be matched (so the consumers
/// DLQ them immediately), while transient errors (connection/deadlock/serialization)
/// must NOT match (so they still retry). EF wraps the driver error, so the unwrap matters.
/// </summary>
public class ConsumerRetryPredicateTests
{
    private static PostgresException Pg(string sqlState) =>
        new(messageText: "boom", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);

    [Theory]
    [InlineData("23503")] // foreign_key_violation
    [InlineData("23505")] // unique_violation
    [InlineData("23502")] // not_null_violation
    [InlineData("23514")] // check_violation
    [InlineData("23P01")] // exclusion_violation
    public void ConstraintViolations_AreMatched(string sqlState)
    {
        ConsumerRetryPredicate.IsConstraintViolation(Pg(sqlState)).Should().BeTrue();
    }

    [Theory]
    [InlineData("08006")] // connection_failure
    [InlineData("40P01")] // deadlock_detected
    [InlineData("40001")] // serialization_failure
    [InlineData("57P03")] // cannot_connect_now
    public void TransientErrors_AreNotMatched(string sqlState)
    {
        ConsumerRetryPredicate.IsConstraintViolation(Pg(sqlState)).Should().BeFalse();
    }

    [Fact]
    public void Match_UnwrapsThroughDbUpdateException()
    {
        // SaveChangesAsync surfaces the driver error wrapped in DbUpdateException.
        var wrapped = new DbUpdateException("update failed", Pg("23505"));
        ConsumerRetryPredicate.IsConstraintViolation(wrapped).Should().BeTrue();
    }

    [Fact]
    public void NonPostgresExceptions_AreNotMatched()
    {
        ConsumerRetryPredicate.IsConstraintViolation(new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public void Null_IsNotMatched()
    {
        ConsumerRetryPredicate.IsConstraintViolation(null).Should().BeFalse();
    }
}
