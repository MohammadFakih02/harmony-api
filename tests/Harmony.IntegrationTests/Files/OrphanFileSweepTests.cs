using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.Services;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harmony.IntegrationTests.Files;

/// <summary>
/// Orphan-attachment GC against the real stack: GetUnconfirmedOlderThanAsync selects only
/// pending rows past the grace window, and OrphanFileSweepService.RunOnceAsync removes them
/// (best-effort deleting the object) while leaving confirmed and still-fresh pending rows.
/// </summary>
public class OrphanFileSweepTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public OrphanFileSweepTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public async Task Sweep_RemovesOldPending_KeepsConfirmedAndFresh()
    {
        var ownerToken = await RegisterAsync("sweepowner", "sweep@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var ownerId = OwnerId(guildId);

        var oldPending = 1001L; // unconfirmed + past grace → swept
        var freshPending = 1002L; // unconfirmed but recent → kept
        var oldConfirmed = 1003L; // confirmed → kept regardless of age

        await SeedAttachmentAsync(oldPending, ownerId, guildId, channelId, confirmed: false, createdAt: Now - 20 * 60_000);
        await SeedAttachmentAsync(freshPending, ownerId, guildId, channelId, confirmed: false, createdAt: Now - 2 * 60_000);
        await SeedAttachmentAsync(oldConfirmed, ownerId, guildId, channelId, confirmed: true, createdAt: Now - 60 * 60_000);

        // The repository query selects only the old pending row.
        using (var scope = Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IFileAttachmentRepository>();
            var orphans = await repo.GetUnconfirmedOlderThanAsync(Now - 15 * 60_000);
            orphans.Select(o => o.Id).Should().BeEquivalentTo(new[] { oldPending });
        }

        // The full sweep removes it (best-effort object delete against MinIO — the never-uploaded
        // key is an idempotent no-op) and leaves the others.
        var sweeper = new OrphanFileSweepService(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OrphanFileSweepService>.Instance
        );
        var swept = await sweeper.RunOnceAsync();
        swept.Should().Be(1);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
            (await db.FileAttachments.AnyAsync(f => f.Id == oldPending)).Should().BeFalse();
            (await db.FileAttachments.AnyAsync(f => f.Id == freshPending)).Should().BeTrue();
            (await db.FileAttachments.AnyAsync(f => f.Id == oldConfirmed)).Should().BeTrue();
        }
    }

    private async Task SeedAttachmentAsync(
        long id,
        long uploaderId,
        long guildId,
        long channelId,
        bool confirmed,
        long createdAt
    )
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
        db.FileAttachments.Add(
            new FileAttachment
            {
                Id = id,
                UploaderId = uploaderId,
                GuildId = guildId,
                ChannelId = channelId,
                MinioKey = $"attachments/{guildId}/{channelId}/{id}",
                Filename = "orphan.png",
                ContentType = "image/png",
                SizeBytes = 10,
                IsConfirmed = confirmed,
                CreatedAt = createdAt,
            }
        );
        await db.SaveChangesAsync();
    }

    private long OwnerId(long guildId)
    {
        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        return guilds.GetByIdAsync(guildId).GetAwaiter().GetResult()!.OwnerId;
    }

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
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Sweep Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildDto>();
        return (guild!.Id, guild.InviteCode!);
    }

    private async Task<long> CreateChannelAsync(string ownerToken, long guildId)
    {
        Auth(ownerToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "general", type = "text", position = 0 }
        );
        resp.EnsureSuccessStatusCode();
        var channel = await resp.Content.ReadFromJsonAsync<ChannelDto>();
        return channel!.Id;
    }

    private record AuthResponse(string AccessToken);

    private record GuildDto(long Id, string Name, string? InviteCode);

    private record ChannelDto(long Id, long? GuildId, string Name, string Type);
}
