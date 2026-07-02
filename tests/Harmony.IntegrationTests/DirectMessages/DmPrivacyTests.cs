using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.DirectMessages;

/// <summary>
/// DM-privacy enforcement: a "friends_only" user can only be DM'd by an accepted friend,
/// but existing conversations stay reachable; "everyone" (the default) always allows.
/// Plus the PATCH /me/dm-privacy persistence + validation.
/// </summary>
public class DmPrivacyTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public DmPrivacyTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<(string token, long userId, string username)> RegisterAsync(
        string username,
        string email
    )
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
        return (body!.AccessToken, body.User.Id, username);
    }

    private void Authorize(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private Task<HttpResponseMessage> OpenDmAsync(long targetUserId) =>
        Client.PostAsJsonAsync("/api/dm", new { targetUserId });

    private Task<HttpResponseMessage> SetDmPrivacyAsync(string dmPrivacy) =>
        Client.PatchAsJsonAsync("/api/users/me/dm-privacy", new { dmPrivacy });

    private Task<HttpResponseMessage> SendDmAsync(long channelId, string content) =>
        Client.PostAsJsonAsync($"/api/dm/{channelId}/messages", new { content });

    private async Task BefriendAsync(
        (string token, long id, string name) a,
        (string token, long id, string name) b
    )
    {
        Authorize(a.token);
        (await Client.PostAsJsonAsync("/api/friends/request", new { username = b.name }))
            .EnsureSuccessStatusCode();
        Authorize(b.token);
        (await Client.PatchAsync($"/api/friends/{a.id}/accept", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task FriendsOnly_BlocksNonFriend_FromOpeningNewDm()
    {
        var a = await RegisterAsync("dmp_a1", "dmp_a1@test.com");
        var b = await RegisterAsync("dmp_b1", "dmp_b1@test.com");

        Authorize(b.token);
        (await SetDmPrivacyAsync("friends_only")).EnsureSuccessStatusCode();

        Authorize(a.token);
        (await OpenDmAsync(b.userId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FriendsOnly_AllowsAcceptedFriend_ToOpenDm()
    {
        var a = await RegisterAsync("dmp_a2", "dmp_a2@test.com");
        var b = await RegisterAsync("dmp_b2", "dmp_b2@test.com");

        await BefriendAsync((a.token, a.userId, a.username), (b.token, b.userId, b.username));

        Authorize(b.token);
        (await SetDmPrivacyAsync("friends_only")).EnsureSuccessStatusCode();

        Authorize(a.token);
        (await OpenDmAsync(b.userId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FriendsOnly_StillAllowsReopeningAnExistingDm()
    {
        var a = await RegisterAsync("dmp_a3", "dmp_a3@test.com");
        var b = await RegisterAsync("dmp_b3", "dmp_b3@test.com");

        // A opens the DM while B is still "everyone".
        Authorize(a.token);
        (await OpenDmAsync(b.userId)).EnsureSuccessStatusCode();

        // B locks down to friends-only — but the existing conversation must remain reachable.
        Authorize(b.token);
        (await SetDmPrivacyAsync("friends_only")).EnsureSuccessStatusCode();

        Authorize(a.token);
        (await OpenDmAsync(b.userId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Everyone_AllowsNonFriend()
    {
        var a = await RegisterAsync("dmp_a4", "dmp_a4@test.com");
        var b = await RegisterAsync("dmp_b4", "dmp_b4@test.com");

        // B's default is "everyone".
        Authorize(a.token);
        (await OpenDmAsync(b.userId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FriendsOnly_BlocksSending_InAnExistingDm_ToANonFriend()
    {
        // The "existing conversation loophole": A opened the DM while B was "everyone", then B
        // locked down. Reopening still works, but A must NOT be able to keep messaging as a stranger.
        var a = await RegisterAsync("dmp_a6", "dmp_a6@test.com");
        var b = await RegisterAsync("dmp_b6", "dmp_b6@test.com");

        Authorize(a.token);
        var open = await OpenDmAsync(b.userId);
        open.EnsureSuccessStatusCode();
        var channelId = (await open.Content.ReadFromJsonAsync<DmChannelDto>())!.ChannelId;

        // Sending is fine while B is "everyone".
        (await SendDmAsync(channelId, "hi")).EnsureSuccessStatusCode();

        Authorize(b.token);
        (await SetDmPrivacyAsync("friends_only")).EnsureSuccessStatusCode();

        // Now A (a non-friend) can no longer send in the existing channel.
        Authorize(a.token);
        (await SendDmAsync(channelId, "still here")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FriendsOnly_AllowsSending_InAnExistingDm_BetweenFriends()
    {
        var a = await RegisterAsync("dmp_a7", "dmp_a7@test.com");
        var b = await RegisterAsync("dmp_b7", "dmp_b7@test.com");
        await BefriendAsync((a.token, a.userId, a.username), (b.token, b.userId, b.username));

        Authorize(b.token);
        (await SetDmPrivacyAsync("friends_only")).EnsureSuccessStatusCode();

        Authorize(a.token);
        var open = await OpenDmAsync(b.userId);
        open.EnsureSuccessStatusCode();
        var channelId = (await open.Content.ReadFromJsonAsync<DmChannelDto>())!.ChannelId;

        (await SendDmAsync(channelId, "hey friend")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task FriendsOnly_BlocksBeingAddedToAGroup_ByANonFriend()
    {
        // The group-DM backdoor: a "friends_only" user can't be pulled into a group by a non-friend.
        var a = await RegisterAsync("dmp_a8", "dmp_a8@test.com");
        var b = await RegisterAsync("dmp_b8", "dmp_b8@test.com"); // a plain second member
        var c = await RegisterAsync("dmp_c8", "dmp_c8@test.com"); // friends_only, not A's friend

        Authorize(c.token);
        (await SetDmPrivacyAsync("friends_only")).EnsureSuccessStatusCode();

        Authorize(a.token);
        var resp = await Client.PostAsJsonAsync(
            "/api/dm/group",
            new { userIds = new[] { b.userId, c.userId } }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateDmPrivacy_Persists_AndValidatesValue()
    {
        var a = await RegisterAsync("dmp_a5", "dmp_a5@test.com");
        Authorize(a.token);

        // Defaults to "everyone".
        var me = await Client.GetFromJsonAsync<MeDto>("/api/users/me");
        me!.DmPrivacy.Should().Be("everyone");

        (await SetDmPrivacyAsync("friends_only")).EnsureSuccessStatusCode();
        (await Client.GetFromJsonAsync<MeDto>("/api/users/me"))!
            .DmPrivacy.Should()
            .Be("friends_only");

        (await SetDmPrivacyAsync("nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record MeDto(string DmPrivacy);

    private record DmChannelDto(long ChannelId);
}
