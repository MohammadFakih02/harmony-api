using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Users;

/// <summary>
/// REST-side tests for friend (per-user, private) nicknames: set/list/clear round-trip, blank PUT
/// clears like DELETE, you can't nickname yourself, and a nickname for an unknown user 404s. The map
/// is owner-scoped — only the caller ever sees their own aliases.
/// </summary>
public class UserNicknameTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public UserNicknameTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<(string token, long userId)> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.User.Id);
    }

    private void Authorize(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task Set_ThenList_ReturnsOwnerScopedMap()
    {
        var (tokenA, _) = await RegisterAsync("nick_a1", "nick_a1@test.com");
        var (tokenB, idB) = await RegisterAsync("nick_b1", "nick_b1@test.com");

        Authorize(tokenA);
        var set = await Client.PutAsJsonAsync($"/api/users/{idB}/nickname", new { nickname = "  Buddy  " });
        set.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var mine = await Client.GetFromJsonAsync<Dictionary<string, string>>("/api/users/me/nicknames");
        mine!.Should().ContainKey(idB.ToString()).WhoseValue.Should().Be("Buddy"); // trimmed

        // The alias is private to A — B sees an empty map.
        Authorize(tokenB);
        var theirs = await Client.GetFromJsonAsync<Dictionary<string, string>>("/api/users/me/nicknames");
        theirs!.Should().BeEmpty();
    }

    [Fact]
    public async Task BlankPut_ClearsLikeDelete()
    {
        var (tokenA, _) = await RegisterAsync("nick_a2", "nick_a2@test.com");
        var (_, idB) = await RegisterAsync("nick_b2", "nick_b2@test.com");

        Authorize(tokenA);
        (await Client.PutAsJsonAsync($"/api/users/{idB}/nickname", new { nickname = "Pal" }))
            .EnsureSuccessStatusCode();

        var blank = await Client.PutAsJsonAsync($"/api/users/{idB}/nickname", new { nickname = "" });
        blank.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var mine = await Client.GetFromJsonAsync<Dictionary<string, string>>("/api/users/me/nicknames");
        mine!.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        var (tokenA, _) = await RegisterAsync("nick_a3", "nick_a3@test.com");
        var (_, idB) = await RegisterAsync("nick_b3", "nick_b3@test.com");

        Authorize(tokenA);
        var del = await Client.DeleteAsync($"/api/users/{idB}/nickname");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Set_ForSelf_Returns400()
    {
        var (tokenA, idA) = await RegisterAsync("nick_a4", "nick_a4@test.com");

        Authorize(tokenA);
        var resp = await Client.PutAsJsonAsync($"/api/users/{idA}/nickname", new { nickname = "Me" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Set_ForUnknownUser_Returns404()
    {
        var (tokenA, _) = await RegisterAsync("nick_a5", "nick_a5@test.com");

        Authorize(tokenA);
        var resp = await Client.PutAsJsonAsync("/api/users/999999999999/nickname", new { nickname = "Ghost" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);
}
