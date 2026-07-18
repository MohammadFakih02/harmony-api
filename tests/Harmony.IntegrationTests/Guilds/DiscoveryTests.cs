using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Infrastructure.Postgres;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// Public-server discovery: only is_public guilds are listed (opt-in via the Overview toggle),
/// name search filters, and joining needs no invite — but a private guild 404s (indistinguishable
/// from missing) and an existing member 409s.
/// </summary>
public class DiscoveryTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public DiscoveryTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<string> RegisterAsync(string username, string email)
    {
        var (token, _) = await RegisterFullAsync(username, email);
        return token;
    }

    private async Task<(string Token, long UserId)> RegisterFullAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<AuthResponse>())!;
        return (body.AccessToken, body.User.Id);
    }

    /// <summary>Directly flips EmailConfirmed — bypasses the email flow for a test that only
    /// cares about the RequireVerifiedEmail gate, not verification itself.</summary>
    private async Task MarkEmailVerifiedAsync(long userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<long> CreateGuildAsync(string name, bool isPublic)
    {
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildDto>();

        if (isPublic)
        {
            var patch = await Client.PatchAsJsonAsync(
                $"/api/guilds/{guild!.Id}",
                new { isPublic = true }
            );
            patch.EnsureSuccessStatusCode();
        }

        return guild!.Id;
    }

    [Fact]
    public async Task Discover_ListsOnlyPublicGuilds()
    {
        var owner = await RegisterAsync("disc_owner", "disc_owner@test.com");
        Auth(owner);
        var publicId = await CreateGuildAsync("Open Harbor", isPublic: true);
        var privateId = await CreateGuildAsync("Secret Cove", isPublic: false);

        var results = await Client.GetFromJsonAsync<List<GuildDto>>("/api/guilds/discover");

        results!.Select(g => g.Id).Should().Contain(publicId).And.NotContain(privateId);
    }

    [Fact]
    public async Task Discover_SearchFiltersByName()
    {
        var owner = await RegisterAsync("disc_search", "disc_search@test.com");
        Auth(owner);
        var harborId = await CreateGuildAsync("Search Harbor", isPublic: true);
        await CreateGuildAsync("Other Place", isPublic: true);

        var results = await Client.GetFromJsonAsync<List<GuildDto>>("/api/guilds/discover?q=harbor");

        results!.Should().OnlyContain(g => g.Id == harborId);
    }

    [Fact]
    public async Task JoinPublicGuild_NoInviteNeeded_AddsTheMember()
    {
        var owner = await RegisterAsync("disc_jowner", "disc_jowner@test.com");
        Auth(owner);
        var guildId = await CreateGuildAsync("Joinable", isPublic: true);

        var joiner = await RegisterAsync("disc_joiner", "disc_joiner@test.com");
        Auth(joiner);

        var join = await Client.PostAsync($"/api/guilds/{guildId}/join", null);
        join.StatusCode.Should().Be(HttpStatusCode.OK);

        // The joiner now sees the guild in their own list.
        var mine = await Client.GetFromJsonAsync<List<GuildDto>>("/api/users/me/guilds");
        mine!.Select(g => g.Id).Should().Contain(guildId);

        // And a second join attempt conflicts.
        var again = await Client.PostAsync($"/api/guilds/{guildId}/join", null);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task JoinPrivateGuild_Returns404_NotLeakingExistence()
    {
        var owner = await RegisterAsync("disc_powner", "disc_powner@test.com");
        Auth(owner);
        var guildId = await CreateGuildAsync("Members Only", isPublic: false);

        var outsider = await RegisterAsync("disc_outsider", "disc_outsider@test.com");
        Auth(outsider);

        var join = await Client.PostAsync($"/api/guilds/{guildId}/join", null);
        join.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task JoinPublicGuild_RequireVerifiedEmail_Returns403_ForUnverifiedJoiner()
    {
        var owner = await RegisterAsync("disc_reqver1", "disc_reqver1@test.com");
        Auth(owner);
        var guildId = await CreateGuildAsync("Verified Only", isPublic: true);
        (await Client.PatchAsJsonAsync($"/api/guilds/{guildId}", new { requireVerifiedEmail = true }))
            .EnsureSuccessStatusCode();

        var (joinerToken, _) = await RegisterFullAsync("disc_reqver1b", "disc_reqver1b@test.com");
        Auth(joinerToken);

        var join = await Client.PostAsync($"/api/guilds/{guildId}/join", null);
        join.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await join.Content.ReadFromJsonAsync<JoinErrorResponse>();
        body!.RequiresVerifiedEmail.Should().BeTrue();
    }

    [Fact]
    public async Task JoinPublicGuild_RequireVerifiedEmail_Succeeds_ForVerifiedJoiner()
    {
        var owner = await RegisterAsync("disc_reqver2", "disc_reqver2@test.com");
        Auth(owner);
        var guildId = await CreateGuildAsync("Verified Only 2", isPublic: true);
        (await Client.PatchAsJsonAsync($"/api/guilds/{guildId}", new { requireVerifiedEmail = true }))
            .EnsureSuccessStatusCode();

        var (joinerToken, joinerId) = await RegisterFullAsync("disc_reqver2b", "disc_reqver2b@test.com");
        await MarkEmailVerifiedAsync(joinerId);
        Auth(joinerToken);

        var join = await Client.PostAsync($"/api/guilds/{guildId}/join", null);
        join.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private record JoinErrorResponse(string Error, bool RequiresVerifiedEmail);

    private record AuthResponse(string AccessToken, AuthUser User);

    private record AuthUser(long Id);

    private record GuildDto(long Id, string Name, bool IsPublic, int MemberCount);
}
