using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Infrastructure.Postgres;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Notifications;

/// <summary>
/// Push-subscription registration: PUT is an endpoint-keyed upsert (a re-subscribe
/// refreshes the keys; a different user logging in on the same browser reassigns the
/// row instead of duplicating it), DELETE is idempotent and owner-scoped, and the
/// public-key endpoint 404s honestly when VAPID keys are unconfigured (the test env).
/// </summary>
public class PushSubscriptionTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public PushSubscriptionTests(HarmonyWebApplicationFactory factory)
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

    private async Task<List<(long UserId, string P256dh)>> RowsForEndpointAsync(string endpoint)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
        return (
            await db.UserPushSubscriptions.Where(s => s.Endpoint == endpoint).ToListAsync()
        )
            .Select(s => (s.UserId, s.P256dh))
            .ToList();
    }

    [Fact]
    public async Task Put_CreatesTheSubscription_AndResubscribeRefreshesKeys()
    {
        var (token, userId) = await RegisterAsync("push_a1", "push_a1@test.com");
        Authorize(token);
        const string endpoint = "https://push.example/sub-a1";

        var first = await Client.PutAsJsonAsync(
            "/api/notifications/push-subscription",
            new { endpoint, p256dh = "key-1", authKey = "auth-1" }
        );
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await Client.PutAsJsonAsync(
            "/api/notifications/push-subscription",
            new { endpoint, p256dh = "key-2", authKey = "auth-2" }
        );
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await RowsForEndpointAsync(endpoint);
        rows.Should().ContainSingle().Which.Should().Be((userId, "key-2"));
    }

    [Fact]
    public async Task Put_EndpointRegisteredToAnotherUser_IsReassignedToTheCaller()
    {
        const string endpoint = "https://push.example/sub-shared";
        var (tokenA, _) = await RegisterAsync("push_b1", "push_b1@test.com");
        var (tokenB, userB) = await RegisterAsync("push_b2", "push_b2@test.com");

        Authorize(tokenA);
        (
            await Client.PutAsJsonAsync(
                "/api/notifications/push-subscription",
                new { endpoint, p256dh = "k", authKey = "a" }
            )
        ).EnsureSuccessStatusCode();

        // Same browser, new login — the endpoint row must follow the new user.
        Authorize(tokenB);
        (
            await Client.PutAsJsonAsync(
                "/api/notifications/push-subscription",
                new { endpoint, p256dh = "k2", authKey = "a2" }
            )
        ).EnsureSuccessStatusCode();

        var rows = await RowsForEndpointAsync(endpoint);
        rows.Should().ContainSingle().Which.UserId.Should().Be(userB);
    }

    [Fact]
    public async Task Delete_IsIdempotent_AndOnlyRemovesTheCallersOwnRow()
    {
        const string endpoint = "https://push.example/sub-del";
        var (tokenA, _) = await RegisterAsync("push_c1", "push_c1@test.com");
        var (tokenB, _) = await RegisterAsync("push_c2", "push_c2@test.com");

        Authorize(tokenA);
        (
            await Client.PutAsJsonAsync(
                "/api/notifications/push-subscription",
                new { endpoint, p256dh = "k", authKey = "a" }
            )
        ).EnsureSuccessStatusCode();

        // Someone else's endpoint: 204 (no existence leak) but the row survives.
        Authorize(tokenB);
        var foreign = await Client.DeleteAsync(
            $"/api/notifications/push-subscription?endpoint={Uri.EscapeDataString(endpoint)}"
        );
        foreign.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RowsForEndpointAsync(endpoint)).Should().ContainSingle();

        // The owner: first delete removes, second is a no-op 204.
        Authorize(tokenA);
        var owned = await Client.DeleteAsync(
            $"/api/notifications/push-subscription?endpoint={Uri.EscapeDataString(endpoint)}"
        );
        owned.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var again = await Client.DeleteAsync(
            $"/api/notifications/push-subscription?endpoint={Uri.EscapeDataString(endpoint)}"
        );
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RowsForEndpointAsync(endpoint)).Should().BeEmpty();
    }

    [Fact]
    public async Task PublicKey_WhenVapidUnconfigured_Returns404()
    {
        // The test environment ships empty WebPush keys — the endpoint must 404 so the
        // client can hide/disable the push toggle instead of subscribing with a junk key.
        var (token, _) = await RegisterAsync("push_d1", "push_d1@test.com");
        Authorize(token);

        var resp = await Client.GetAsync("/api/notifications/push/public-key");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);
}
