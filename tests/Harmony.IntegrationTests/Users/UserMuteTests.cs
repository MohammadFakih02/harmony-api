using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Harmony.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Users;

/// <summary>
/// REST + repository tests for user-mutes: mute/unmute CRUD with upsert semantics,
/// active-only listing (expired-but-unswept rows excluded), validation, and the
/// DeleteExpiredAsync sweep seam that MuteExpiryService drives.
/// </summary>
public class UserMuteTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public UserMuteTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<(string token, long userId)> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username,
                email,
                password = "Password123!",
            }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.User.Id);
    }

    private void Authorize(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static long FutureMs(int minutes) =>
        DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Mute_ThenList_ReturnsActiveMute()
    {
        var (token, _) = await RegisterAsync("muteuser1", "mute1@test.com");
        Authorize(token);

        var mute = await Client.PostAsJsonAsync(
            "/api/mutes",
            new
            {
                targetType = "guild",
                targetId = 555L,
                mutedUntil = (long?)null,
            }
        );
        mute.EnsureSuccessStatusCode();

        var list = await Client.GetFromJsonAsync<List<MuteDto>>("/api/mutes");
        list.Should().ContainSingle(m => m.TargetId == 555 && m.TargetType == "guild" && m.MutedUntil == null);
    }

    [Fact]
    public async Task Mute_SameTargetTwice_UpsertsExpiry()
    {
        var (token, _) = await RegisterAsync("muteuser2", "mute2@test.com");
        Authorize(token);

        await Client.PostAsJsonAsync("/api/mutes", new { targetType = "channel", targetId = 7L, mutedUntil = FutureMs(10) });
        var second = FutureMs(120);
        (await Client.PostAsJsonAsync("/api/mutes", new { targetType = "channel", targetId = 7L, mutedUntil = second }))
            .EnsureSuccessStatusCode();

        var list = await Client.GetFromJsonAsync<List<MuteDto>>("/api/mutes");
        list.Should().ContainSingle(m => m.TargetId == 7);
        list!.Single(m => m.TargetId == 7).MutedUntil.Should().Be(second);
    }

    [Fact]
    public async Task Unmute_RemovesTheMute()
    {
        var (token, _) = await RegisterAsync("muteuser3", "mute3@test.com");
        Authorize(token);

        (await Client.PostAsJsonAsync("/api/mutes", new { targetType = "user", targetId = 42L, mutedUntil = (long?)null }))
            .EnsureSuccessStatusCode();

        var unmute = await Client.DeleteAsync("/api/mutes/user/42");
        unmute.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Client.GetFromJsonAsync<List<MuteDto>>("/api/mutes");
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task Unmute_WhenNotMuted_IsIdempotent()
    {
        var (token, _) = await RegisterAsync("muteuser4", "mute4@test.com");
        Authorize(token);

        var unmute = await Client.DeleteAsync("/api/mutes/guild/999");
        unmute.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Mute_WithInvalidTargetType_Returns400()
    {
        var (token, _) = await RegisterAsync("muteuser5", "mute5@test.com");
        Authorize(token);

        var resp = await Client.PostAsJsonAsync("/api/mutes", new { targetType = "planet", targetId = 1L, mutedUntil = (long?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Mute_WithPastExpiry_Returns400()
    {
        var (token, _) = await RegisterAsync("muteuser6", "mute6@test.com");
        Authorize(token);

        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        var resp = await Client.PostAsJsonAsync("/api/mutes", new { targetType = "guild", targetId = 1L, mutedUntil = past });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetMutes_ExcludesExpiredButUnsweptRows()
    {
        var (token, userId) = await RegisterAsync("muteuser7", "mute7@test.com");
        Authorize(token);

        // Seed one expired and one active mute directly (bypassing the future-expiry validator).
        await SeedMuteAsync(userId, 11, "guild", mutedUntil: 1);                 // long past
        await SeedMuteAsync(userId, 22, "channel", mutedUntil: FutureMs(30));    // active

        var list = await Client.GetFromJsonAsync<List<MuteDto>>("/api/mutes");
        list.Should().ContainSingle(m => m.TargetId == 22);
        list.Should().NotContain(m => m.TargetId == 11);
    }

    [Fact]
    public async Task DeleteExpiredAsync_RemovesOnlyExpired_AndReturnsThem()
    {
        var (_, userId) = await RegisterAsync("muteuser8", "mute8@test.com");

        await SeedMuteAsync(userId, 11, "guild", mutedUntil: 1);                 // expired
        await SeedMuteAsync(userId, 22, "channel", mutedUntil: FutureMs(30));    // active
        await SeedMuteAsync(userId, 33, "user", mutedUntil: null);               // indefinite

        using var scope = Factory.Services.CreateScope();
        var mutes = scope.ServiceProvider.GetRequiredService<IUserMuteRepository>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var swept = await mutes.DeleteExpiredAsync(now);

        swept.Should().ContainSingle(m => m.TargetId == 11);
        (await mutes.GetActiveMutesAsync(userId, now)).Select(m => m.TargetId)
            .Should().BeEquivalentTo(new[] { 22L, 33L });
    }

    private async Task SeedMuteAsync(long userId, long targetId, string type, long? mutedUntil)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
        db.UserMutes.Add(
            new UserMute
            {
                UserId = userId,
                TargetId = targetId,
                TargetType = type,
                MutedUntil = mutedUntil,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await db.SaveChangesAsync();
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record MuteDto(string TargetType, long TargetId, long? MutedUntil, long CreatedAt);
}
