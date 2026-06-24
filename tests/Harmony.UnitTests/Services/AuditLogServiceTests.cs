using System.Text.Json;
using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Unit tests for the audit-log write seam: it stamps a snowflake id, serializes the optional
/// changes object into the jsonb column, and — critically — never lets its own failure bubble
/// out (best-effort, so a moderation action can't be failed by the audit write).
/// </summary>
public class AuditLogServiceTests
{
    private const long GuildId = 100;
    private const long ActorId = 200;
    private const long TargetId = 300;

    private static (AuditLogService sut, Mock<IAuditLogRepository> repo, Mock<ISnowflakeIdGenerator> snowflake)
        BuildSut()
    {
        var repo = new Mock<IAuditLogRepository>();
        var snowflake = new Mock<ISnowflakeIdGenerator>();
        snowflake.Setup(s => s.NextId()).Returns(999);
        var sut = new AuditLogService(repo.Object, snowflake.Object, NullLogger<AuditLogService>.Instance);
        return (sut, repo, snowflake);
    }

    [Fact]
    public async Task LogAsync_PersistsEntry_WithSnowflakeId_AndSerializedChanges()
    {
        var (sut, repo, _) = BuildSut();
        AuditLog? captured = null;
        repo.Setup(r => r.AddAsync(It.IsAny<AuditLog>()))
            .Callback<AuditLog>(a => captured = a)
            .Returns(Task.CompletedTask);

        await sut.LogAsync(
            GuildId,
            ActorId,
            AuditLogAction.MessageDelete,
            targetId: TargetId,
            changes: new { channelId = 42L, authorId = 7L },
            reason: "spam"
        );

        repo.Verify(r => r.AddAsync(It.IsAny<AuditLog>()), Times.Once);
        repo.Verify(r => r.SaveChangesAsync(), Times.Once);

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(999);
        captured.GuildId.Should().Be(GuildId);
        captured.ActorId.Should().Be(ActorId);
        captured.ActionType.Should().Be(AuditLogAction.MessageDelete);
        captured.TargetId.Should().Be(TargetId);
        captured.Reason.Should().Be("spam");
        captured.CreatedAt.Should().BeGreaterThan(0);

        captured.Changes.Should().NotBeNull();
        var json = JsonDocument.Parse(captured.Changes!);
        json.RootElement.GetProperty("channelId").GetInt64().Should().Be(42);
        json.RootElement.GetProperty("authorId").GetInt64().Should().Be(7);
    }

    [Fact]
    public async Task LogAsync_WithNullChanges_StoresNullJson()
    {
        var (sut, repo, _) = BuildSut();
        AuditLog? captured = null;
        repo.Setup(r => r.AddAsync(It.IsAny<AuditLog>()))
            .Callback<AuditLog>(a => captured = a)
            .Returns(Task.CompletedTask);

        await sut.LogAsync(GuildId, ActorId, AuditLogAction.RoleCreate);

        captured!.Changes.Should().BeNull();
        captured.TargetId.Should().BeNull();
        captured.Reason.Should().BeNull();
    }

    [Fact]
    public async Task LogAsync_SwallowsRepositoryFailure_BestEffort()
    {
        var (sut, repo, _) = BuildSut();
        repo.Setup(r => r.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("db down"));

        // Must not throw — a failed audit write can never fail the action that triggered it.
        var act = async () => await sut.LogAsync(GuildId, ActorId, AuditLogAction.MemberKick);
        await act.Should().NotThrowAsync();
    }
}
