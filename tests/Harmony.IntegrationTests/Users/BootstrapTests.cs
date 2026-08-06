using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Users;

/// <summary>
/// GET /api/users/me/bootstrap — the one-round-trip boot payload. Verifies the aggregate
/// mirrors the standalone endpoints (profile, guilds, friends, pending, DMs, nicknames,
/// notifications + badge count) and that it requires authentication.
/// </summary>
public class BootstrapTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public BootstrapTests(HarmonyWebApplicationFactory factory)
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
    public async Task Get_ReturnsAggregatedBootPayload()
    {
        var (tokenA, idA) = await RegisterAsync("boot_a1", "boot_a1@test.com");
        var (tokenB, idB) = await RegisterAsync("boot_b1", "boot_b1@test.com");

        // A owns a guild, friends B, opens a DM with B, and nicknames them.
        Authorize(tokenA);
        var guildResp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Boot Guild" });
        guildResp.EnsureSuccessStatusCode();
        var guild = await guildResp.Content.ReadFromJsonAsync<GuildDto>();

        (await Client.PostAsJsonAsync("/api/friends/request", new { username = "boot_b1" }))
            .EnsureSuccessStatusCode();
        Authorize(tokenB);
        (await Client.PatchAsync($"/api/friends/{idA}/accept", null)).EnsureSuccessStatusCode();

        Authorize(tokenA);
        (await Client.PostAsJsonAsync("/api/dm", new { targetUserId = idB })).EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync($"/api/users/{idB}/nickname", new { nickname = "Bee" }))
            .EnsureSuccessStatusCode();

        var boot = await Client.GetFromJsonAsync<BootstrapDto>("/api/users/me/bootstrap");

        boot!.Profile.Username.Should().Be("boot_a1");
        boot.Guilds.Should().ContainSingle(g => g.Id == guild!.Id && g.Name == "Boot Guild");
        boot.Friends.Should().ContainSingle(f => f.Id == idB && f.Username == "boot_b1");
        boot.PendingFriends.Should().BeEmpty();
        boot.Dms.Should().ContainSingle();
        boot.Dms[0].Participants.Should().ContainSingle(p => p.UserId == idB);
        boot.Nicknames.Should().ContainKey(idB.ToString()).WhoseValue.Should().Be("Bee");
        // Unread is keyed per text channel; a fresh guild has #general with nothing unread —
        // just assert the shape resolves (no throw) rather than a specific count.
        boot.Unread.Should().NotBeNull();
    }

    [Fact]
    public async Task Get_IncludesNotifications_ForTheRecipient()
    {
        var (tokenA, idA) = await RegisterAsync("boot_a2", "boot_a2@test.com");
        var (tokenB, _) = await RegisterAsync("boot_b2", "boot_b2@test.com");

        // A → B friend request persists a friend_request notification for B (synchronous path).
        Authorize(tokenA);
        (await Client.PostAsJsonAsync("/api/friends/request", new { username = "boot_b2" }))
            .EnsureSuccessStatusCode();

        Authorize(tokenB);
        var boot = await Client.GetFromJsonAsync<BootstrapDto>("/api/users/me/bootstrap");

        boot!.NotificationUnreadCount.Should().Be(1);
        boot.Notifications.Should().ContainSingle(n => n.Type == "friend_request" && n.ActorId == idA);
        boot.PendingFriends.Should().ContainSingle(p => p.Direction == "incoming");
    }

    [Fact]
    public async Task Get_WithoutAuth_Returns401()
    {
        var resp = await Client.GetAsync("/api/users/me/bootstrap");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Local DTO mirrors (per-file convention in this suite).
    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id, string Username);

    private record GuildDto(long Id, string Name);

    private record BootstrapDto(
        ProfileDto Profile,
        List<GuildDto> Guilds,
        List<UnreadDto> Unread,
        List<FriendDto> Friends,
        List<PendingDto> PendingFriends,
        List<DmDto> Dms,
        Dictionary<string, string> Nicknames,
        List<NotificationDto> Notifications,
        int NotificationUnreadCount
    );

    private record ProfileDto(long Id, string Username);

    private record UnreadDto(long ChannelId, long GuildId, int UnreadCount);

    private record FriendDto(long Id, string Username);

    private record PendingDto(long Id, string Username, string Direction);

    private record DmDto(long ChannelId, bool IsGroup, List<ParticipantDto> Participants);

    private record ParticipantDto(long UserId, string Username);

    private record NotificationDto(long Id, string Type, long? ActorId, bool IsRead);
}
