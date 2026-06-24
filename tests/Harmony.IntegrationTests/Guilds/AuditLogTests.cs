using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// The audit-log read endpoint against real Postgres: entries written through the
/// IAuditLogService seam come back enriched with the actor's identity, ViewAuditLog gates the
/// view (the owner passes, a plain member is forbidden), and the action filter + before cursor
/// page correctly.
/// </summary>
public class AuditLogTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public AuditLogTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<string> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.AccessToken;
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<(long guildId, string inviteCode)> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Audit Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        return (guild!.Id, guild.InviteCode!);
    }

    private async Task<long> JoinAndGetMemberIdAsync(string memberToken, string invite, long guildId, long ownerId)
    {
        Auth(memberToken);
        var joinResp = await Client.PostAsJsonAsync($"/api/guilds/join/{invite}", new { });
        joinResp.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        return (await guilds.GetMemberIdsAsync(guildId)).Single(id => id != ownerId);
    }

    private long OwnerIdOf(long guildId)
    {
        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        return guilds.GetByIdAsync(guildId).GetAwaiter().GetResult()!.OwnerId;
    }

    private async Task LogAsync(
        long guildId,
        long actorId,
        string actionType,
        long? targetId = null,
        object? changes = null,
        string? reason = null
    )
    {
        using var scope = Factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        await audit.LogAsync(guildId, actorId, actionType, targetId, changes, reason);
    }

    [Fact]
    public async Task Owner_SeesEntries_EnrichedWithActorIdentity()
    {
        var token = await RegisterAsync("auditowner1", "auditowner1@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var ownerId = OwnerIdOf(guildId);

        await LogAsync(
            guildId,
            ownerId,
            AuditLogAction.MemberKick,
            targetId: 42,
            changes: new { reasonCode = "spam" },
            reason: "spamming"
        );

        Auth(token);
        var entries = await Client.GetFromJsonAsync<List<AuditLogEntryResponse>>(
            $"/api/guilds/{guildId}/audit-log"
        );

        entries.Should().ContainSingle();
        var entry = entries!.Single();
        entry.ActorId.Should().Be(ownerId);
        entry.ActorUsername.Should().Be("auditowner1");
        entry.ActionType.Should().Be(AuditLogAction.MemberKick);
        entry.TargetId.Should().Be(42);
        entry.Reason.Should().Be("spamming");
        entry.Changes.Should().Contain("reasonCode");
    }

    [Fact]
    public async Task PlainMember_WithoutViewAuditLog_IsForbidden()
    {
        var ownerToken = await RegisterAsync("auditowner2", "auditowner2@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var ownerId = OwnerIdOf(guildId);

        var memberToken = await RegisterAsync("auditmember2", "auditmember2@test.com");
        await JoinAndGetMemberIdAsync(memberToken, invite, guildId, ownerId);

        Auth(memberToken);
        var resp = await Client.GetAsync($"/api/guilds/{guildId}/audit-log");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ActionFilter_AndBeforeCursor_PageCorrectly()
    {
        var token = await RegisterAsync("auditowner3", "auditowner3@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var ownerId = OwnerIdOf(guildId);

        // Three entries, oldest → newest (snowflake ids increase, so the last is the newest).
        await LogAsync(guildId, ownerId, AuditLogAction.RoleCreate);
        await LogAsync(guildId, ownerId, AuditLogAction.MemberKick);
        await LogAsync(guildId, ownerId, AuditLogAction.RoleCreate);

        Auth(token);

        // Filter by action.
        var kicks = await Client.GetFromJsonAsync<List<AuditLogEntryResponse>>(
            $"/api/guilds/{guildId}/audit-log?action={AuditLogAction.MemberKick}"
        );
        kicks.Should().ContainSingle().Which.ActionType.Should().Be(AuditLogAction.MemberKick);

        // Newest-first page of size 1, then page older via the before cursor.
        var firstPage = await Client.GetFromJsonAsync<List<AuditLogEntryResponse>>(
            $"/api/guilds/{guildId}/audit-log?limit=1"
        );
        firstPage.Should().ContainSingle();
        var newest = firstPage!.Single();

        var nextPage = await Client.GetFromJsonAsync<List<AuditLogEntryResponse>>(
            $"/api/guilds/{guildId}/audit-log?limit=1&before={newest.Id}"
        );
        nextPage.Should().ContainSingle();
        nextPage!.Single().Id.Should().BeLessThan(newest.Id);
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(long Id, string Name, string? InviteCode);

    private record AuditLogEntryResponse(
        long Id,
        long ActorId,
        string? ActorUsername,
        string? ActorAvatarKey,
        string ActionType,
        long? TargetId,
        string? Changes,
        string? Reason,
        long CreatedAt
    );
}
