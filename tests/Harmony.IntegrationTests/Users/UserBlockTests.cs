using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Users;

/// <summary>
/// REST-side tests for user-blocks: block/unblock are idempotent, the block list
/// returns the blocked user's public identity, self/nonexistent targets are rejected,
/// and the bidirectional AreBlockedAsync seam (consumed by Phase 4 DM/mention/presence)
/// sees a block from either side.
/// </summary>
public class UserBlockTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public UserBlockTests(HarmonyWebApplicationFactory factory)
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

    [Fact]
    public async Task Block_ThenList_ReturnsBlockedUser()
    {
        var (tokenA, _) = await RegisterAsync("blockuser1", "block1@test.com");
        var (_, idB) = await RegisterAsync("blockuser2", "block2@test.com");
        Authorize(tokenA);

        var block = await Client.PostAsync($"/api/users/{idB}/block", null);
        block.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Client.GetFromJsonAsync<List<BlockDto>>("/api/users/me/blocks");
        list.Should().ContainSingle(b => b.Id == idB && b.Username == "blockuser2");
    }

    [Fact]
    public async Task Block_IsIdempotent()
    {
        var (tokenA, _) = await RegisterAsync("blockuser3", "block3@test.com");
        var (_, idB) = await RegisterAsync("blockuser4", "block4@test.com");
        Authorize(tokenA);

        (await Client.PostAsync($"/api/users/{idB}/block", null)).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/api/users/{idB}/block", null)).EnsureSuccessStatusCode();

        var list = await Client.GetFromJsonAsync<List<BlockDto>>("/api/users/me/blocks");
        list.Should().HaveCount(1);
    }

    [Fact]
    public async Task Unblock_RemovesTheBlock()
    {
        var (tokenA, _) = await RegisterAsync("blockuser5", "block5@test.com");
        var (_, idB) = await RegisterAsync("blockuser6", "block6@test.com");
        Authorize(tokenA);

        (await Client.PostAsync($"/api/users/{idB}/block", null)).EnsureSuccessStatusCode();

        var unblock = await Client.DeleteAsync($"/api/users/{idB}/block");
        unblock.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Client.GetFromJsonAsync<List<BlockDto>>("/api/users/me/blocks");
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task Unblock_WhenNotBlocked_IsIdempotent()
    {
        var (tokenA, _) = await RegisterAsync("blockuser7", "block7@test.com");
        var (_, idB) = await RegisterAsync("blockuser8", "block8@test.com");
        Authorize(tokenA);

        var unblock = await Client.DeleteAsync($"/api/users/{idB}/block");
        unblock.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Block_Self_Returns400()
    {
        var (tokenA, idA) = await RegisterAsync("blockuser9", "block9@test.com");
        Authorize(tokenA);

        var resp = await Client.PostAsync($"/api/users/{idA}/block", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Block_NonexistentUser_Returns404()
    {
        var (tokenA, _) = await RegisterAsync("blockuser10", "block10@test.com");
        Authorize(tokenA);

        var resp = await Client.PostAsync("/api/users/999999999999/block", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AreBlocked_IsTrueFromEitherDirection()
    {
        var (tokenA, idA) = await RegisterAsync("blockuser11", "block11@test.com");
        var (_, idB) = await RegisterAsync("blockuser12", "block12@test.com");

        // A blocks B over the API.
        Authorize(tokenA);
        (await Client.PostAsync($"/api/users/{idB}/block", null)).EnsureSuccessStatusCode();

        // The seam reports the pair as blocked regardless of argument order.
        using var scope = Factory.Services.CreateScope();
        var blocks = scope.ServiceProvider.GetRequiredService<IUserBlockRepository>();

        (await blocks.AreBlockedAsync(idA, idB)).Should().BeTrue();
        (await blocks.AreBlockedAsync(idB, idA)).Should().BeTrue();
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record BlockDto(long Id, string Username);
}
