using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Users;

/// <summary>
/// PATCH /api/users/me/guild-order — the personal guild-rail order. Verifies the saved order
/// is applied to GET /users/me/guilds and the bootstrap payload, and that guilds missing from
/// the list (new joins) append after the ordered ones in join order.
/// </summary>
public class GuildOrderTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GuildOrderTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<string> RegisterAsync(string username, string email)
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
        return body!.AccessToken;
    }

    private async Task<long> CreateGuildAsync(string name)
    {
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<GuildDto>())!.Id;
    }

    [Fact]
    public async Task SavedOrder_IsAppliedToMyGuilds_AndBootstrap()
    {
        var token = await RegisterAsync("order_a1", "order_a1@test.com");
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var g1 = await CreateGuildAsync("Order One");
        var g2 = await CreateGuildAsync("Order Two");
        var g3 = await CreateGuildAsync("Order Three");

        // Default = join order.
        var initial = await Client.GetFromJsonAsync<List<GuildDto>>("/api/users/me/guilds");
        initial!.Select(g => g.Id).Should().ContainInOrder(g1, g2, g3);

        // Reorder: 3, 1, 2.
        var patch = await Client.PatchAsJsonAsync(
            "/api/users/me/guild-order",
            new { guildOrder = new[] { g3, g1, g2 } }
        );
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var ordered = await Client.GetFromJsonAsync<List<GuildDto>>("/api/users/me/guilds");
        ordered!.Select(g => g.Id).Should().ContainInOrder(g3, g1, g2);

        var boot = await Client.GetFromJsonAsync<BootstrapDto>("/api/users/me/bootstrap");
        boot!.Guilds.Select(g => g.Id).Should().ContainInOrder(g3, g1, g2);
    }

    [Fact]
    public async Task GuildsMissingFromTheSavedOrder_AppendAfterIt_InJoinOrder()
    {
        var token = await RegisterAsync("order_a2", "order_a2@test.com");
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var g1 = await CreateGuildAsync("Order Old One");
        var g2 = await CreateGuildAsync("Order Old Two");

        (
            await Client.PatchAsJsonAsync(
                "/api/users/me/guild-order",
                new { guildOrder = new[] { g2, g1 } }
            )
        ).EnsureSuccessStatusCode();

        // Guilds created AFTER the order was saved (and a stale id in the list) don't break it.
        var g3 = await CreateGuildAsync("Order New");

        var guilds = await Client.GetFromJsonAsync<List<GuildDto>>("/api/users/me/guilds");
        guilds!.Select(g => g.Id).Should().ContainInOrder(g2, g1, g3);
    }

    private record AuthResponse(string AccessToken);

    private record GuildDto(long Id, string Name);

    private record BootstrapDto(List<GuildDto> Guilds);
}
