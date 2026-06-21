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
/// REST-side tests for friends-system: request/accept lifecycle, the auto-accept on a
/// mutual request, decline/cancel/unfriend, the blocking interactions (a block both
/// rejects new requests and severs an existing friendship), and the GetFriendIdsAsync
/// seam that RedisPresenceService consumes to fan presence out to friends.
/// </summary>
public class FriendTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public FriendTests(HarmonyWebApplicationFactory factory)
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

    private Task<HttpResponseMessage> SendRequestAsync(string username) =>
        Client.PostAsJsonAsync("/api/friends/request", new { username });

    [Fact]
    public async Task Request_ThenList_ShowsOutgoingAndIncomingPending()
    {
        var (tokenA, idA) = await RegisterAsync("friend_a1", "friend_a1@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b1", "friend_b1@test.com");

        Authorize(tokenA);
        (await SendRequestAsync("friend_b1")).EnsureSuccessStatusCode();

        var outgoing = await Client.GetFromJsonAsync<List<PendingDto>>("/api/friends/pending");
        outgoing.Should().ContainSingle(p => p.Id == idB && p.Direction == "outgoing");

        Authorize(tokenB);
        var incoming = await Client.GetFromJsonAsync<List<PendingDto>>("/api/friends/pending");
        incoming.Should().ContainSingle(p => p.Id == idA && p.Direction == "incoming");
    }

    [Fact]
    public async Task Accept_MakesBothPartiesFriends_AndClearsPending()
    {
        var (tokenA, idA) = await RegisterAsync("friend_a2", "friend_a2@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b2", "friend_b2@test.com");

        Authorize(tokenA);
        (await SendRequestAsync("friend_b2")).EnsureSuccessStatusCode();

        Authorize(tokenB);
        var accept = await Client.PatchAsync($"/api/friends/{idA}/accept", null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        var bFriends = await Client.GetFromJsonAsync<List<FriendDto>>("/api/friends");
        bFriends.Should().ContainSingle(f => f.Id == idA);
        (await Client.GetFromJsonAsync<List<PendingDto>>("/api/friends/pending")).Should().BeEmpty();

        Authorize(tokenA);
        var aFriends = await Client.GetFromJsonAsync<List<FriendDto>>("/api/friends");
        aFriends.Should().ContainSingle(f => f.Id == idB);
    }

    [Fact]
    public async Task Request_WhenReverseAlreadyPending_AutoAccepts()
    {
        var (tokenA, idA) = await RegisterAsync("friend_a3", "friend_a3@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b3", "friend_b3@test.com");

        // A requests B, then B "requests" A back → the existing request is accepted.
        Authorize(tokenA);
        (await SendRequestAsync("friend_b3")).EnsureSuccessStatusCode();

        Authorize(tokenB);
        var resp = await SendRequestAsync("friend_a3");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var bFriends = await Client.GetFromJsonAsync<List<FriendDto>>("/api/friends");
        bFriends.Should().ContainSingle(f => f.Id == idA);
        (await Client.GetFromJsonAsync<List<PendingDto>>("/api/friends/pending")).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_Cancels_PendingRequest()
    {
        var (tokenA, _) = await RegisterAsync("friend_a4", "friend_a4@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b4", "friend_b4@test.com");

        Authorize(tokenA);
        (await SendRequestAsync("friend_b4")).EnsureSuccessStatusCode();

        var del = await Client.DeleteAsync($"/api/friends/{idB}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client.GetFromJsonAsync<List<PendingDto>>("/api/friends/pending")).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_Unfriends_AcceptedFriend()
    {
        var (tokenA, idA) = await RegisterAsync("friend_a5", "friend_a5@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b5", "friend_b5@test.com");

        Authorize(tokenA);
        (await SendRequestAsync("friend_b5")).EnsureSuccessStatusCode();
        Authorize(tokenB);
        (await Client.PatchAsync($"/api/friends/{idA}/accept", null)).EnsureSuccessStatusCode();

        var del = await Client.DeleteAsync($"/api/friends/{idA}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client.GetFromJsonAsync<List<FriendDto>>("/api/friends")).Should().BeEmpty();
        Authorize(tokenA);
        (await Client.GetFromJsonAsync<List<FriendDto>>("/api/friends")).Should().BeEmpty();
    }

    [Fact]
    public async Task Request_ToSelf_Returns400()
    {
        var (tokenA, _) = await RegisterAsync("friend_a6", "friend_a6@test.com");
        Authorize(tokenA);

        var resp = await SendRequestAsync("friend_a6");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Request_ToUnknownUsername_Returns404()
    {
        var (tokenA, _) = await RegisterAsync("friend_a7", "friend_a7@test.com");
        Authorize(tokenA);

        var resp = await SendRequestAsync("no_such_user_xyz");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Request_DuplicatePending_Returns409()
    {
        var (tokenA, _) = await RegisterAsync("friend_a8", "friend_a8@test.com");
        await RegisterAsync("friend_b8", "friend_b8@test.com");
        Authorize(tokenA);

        (await SendRequestAsync("friend_b8")).EnsureSuccessStatusCode();
        var dup = await SendRequestAsync("friend_b8");
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Request_WhenBlocked_Returns403()
    {
        var (tokenA, _) = await RegisterAsync("friend_a9", "friend_a9@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b9", "friend_b9@test.com");

        // A blocks B → A cannot then friend-request B.
        Authorize(tokenA);
        (await Client.PostAsync($"/api/users/{idB}/block", null)).EnsureSuccessStatusCode();

        var resp = await SendRequestAsync("friend_b9");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Block_SeversExistingFriendship()
    {
        var (tokenA, idA) = await RegisterAsync("friend_a10", "friend_a10@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b10", "friend_b10@test.com");

        Authorize(tokenA);
        (await SendRequestAsync("friend_b10")).EnsureSuccessStatusCode();
        Authorize(tokenB);
        (await Client.PatchAsync($"/api/friends/{idA}/accept", null)).EnsureSuccessStatusCode();

        // A blocks B → the friendship is removed for both.
        Authorize(tokenA);
        (await Client.PostAsync($"/api/users/{idB}/block", null)).EnsureSuccessStatusCode();

        (await Client.GetFromJsonAsync<List<FriendDto>>("/api/friends")).Should().BeEmpty();
        Authorize(tokenB);
        (await Client.GetFromJsonAsync<List<FriendDto>>("/api/friends")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetFriendIds_Seam_ReturnsAcceptedFriend_BothDirections()
    {
        var (tokenA, idA) = await RegisterAsync("friend_a11", "friend_a11@test.com");
        var (tokenB, idB) = await RegisterAsync("friend_b11", "friend_b11@test.com");

        Authorize(tokenA);
        (await SendRequestAsync("friend_b11")).EnsureSuccessStatusCode();
        Authorize(tokenB);
        (await Client.PatchAsync($"/api/friends/{idA}/accept", null)).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var friends = scope.ServiceProvider.GetRequiredService<IFriendRepository>();

        (await friends.GetFriendIdsAsync(idA)).Should().ContainSingle().Which.Should().Be(idB);
        (await friends.GetFriendIdsAsync(idB)).Should().ContainSingle().Which.Should().Be(idA);
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record FriendDto(long Id, string Username, long Since);

    private record PendingDto(long Id, string Username, string Direction, long CreatedAt);
}
