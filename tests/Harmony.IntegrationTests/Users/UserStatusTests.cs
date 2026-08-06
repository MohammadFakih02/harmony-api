using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Users;

/// <summary>
/// REST-side tests for the manual status feature: PATCH /api/users/me/status durably
/// persists the preferred status (survives a fresh GET /me), the validator rejects
/// unknown values, and GET /api/users/presence returns effective statuses in bulk.
/// </summary>
public class UserStatusTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public UserStatusTests(HarmonyWebApplicationFactory factory)
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
    public async Task PatchStatus_PersistsPreferredStatus_AcrossGetMe()
    {
        var (token, _) = await RegisterAsync("statususer1", "status1@test.com");
        Authorize(token);

        // Default is online.
        var me0 = await (await Client.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<ProfileDto>();
        me0!.PreferredStatus.Should().Be("online");

        var patch = await Client.PatchAsJsonAsync("/api/users/me/status", new { status = "dnd" });
        patch.EnsureSuccessStatusCode();
        var patched = await patch.Content.ReadFromJsonAsync<ProfileDto>();
        patched!.PreferredStatus.Should().Be("dnd");

        // Durable: a fresh read reflects it (Postgres, not just the Redis cache).
        var me1 = await (await Client.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<ProfileDto>();
        me1!.PreferredStatus.Should().Be("dnd");
    }

    [Fact]
    public async Task PatchStatus_WithInvalidValue_Returns400()
    {
        var (token, _) = await RegisterAsync("statususer2", "status2@test.com");
        Authorize(token);

        var resp = await Client.PatchAsJsonAsync("/api/users/me/status", new { status = "banana" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetPresence_ReturnsEffectiveStatuses_ForConnectedUsers()
    {
        var (tokenA, idA) = await RegisterAsync("statususer3", "status3@test.com");
        var (_, idB) = await RegisterAsync("statususer4", "status4@test.com");

        // A sets dnd (no live hub connection needed — PATCH updates the cache, but the
        // public status key only exists while connected, so A reads offline here). This
        // asserts the bulk endpoint shape + offline defaulting rather than live presence,
        // which PresenceFlowTests covers end-to-end.
        Authorize(tokenA);
        (await Client.PatchAsJsonAsync("/api/users/me/status", new { status = "dnd" }))
            .EnsureSuccessStatusCode();

        var resp = await Client.GetAsync($"/api/users/presence?ids={idA},{idB}");
        resp.EnsureSuccessStatusCode();
        var map = await resp.Content.ReadFromJsonAsync<Dictionary<string, PresenceDto>>();

        map.Should().NotBeNull();
        map![idA.ToString()].Status.Should().Be("offline"); // not connected → no public status key
        map[idB.ToString()].Status.Should().Be("offline");
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record ProfileDto(long Id, string Username, string PreferredStatus);

    private record PresenceDto(string Status, string? StatusMessage);
}
