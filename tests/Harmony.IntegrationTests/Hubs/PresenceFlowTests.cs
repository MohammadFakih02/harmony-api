using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Hubs;

/// <summary>
/// End-to-end presence flow against real Redis: ChatHub.OnConnectedAsync/
/// OnDisconnectedAsync/Heartbeat → RedisPresenceService → user:{id}:status.
///
/// There's no friends-system yet (Phase 4), so OnlineStatus/OfflineStatus have no
/// recipients to deliver to by design — these tests verify the Redis-backed status
/// transitions via IPresenceService.GetStatusAsync rather than a received broadcast.
/// </summary>
public class PresenceFlowTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public PresenceFlowTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private HubConnection BuildConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, $"hubs/chat?access_token={accessToken}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                }
            )
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString
            )
            .Build();

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

    private Task<string> GetStatusAsync(long userId)
    {
        using var scope = Factory.Services.CreateScope();
        var presence = scope.ServiceProvider.GetRequiredService<IPresenceService>();
        return presence.GetStatusAsync(userId);
    }

    [Fact]
    public async Task Connect_MarksUserOnline()
    {
        var (token, userId) = await RegisterAsync("presenceuser1", "presence1@test.com");
        var conn = BuildConnection(token);

        await conn.StartAsync();
        try
        {
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "online"
            );
        }
        finally
        {
            await conn.StopAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disconnect_LastConnection_MarksUserOffline()
    {
        var (token, userId) = await RegisterAsync("presenceuser2", "presence2@test.com");
        var conn = BuildConnection(token);

        await conn.StartAsync();
        await Eventually.GetAsync(
            action: () => GetStatusAsync(userId),
            predicate: s => s == "online"
        );

        await conn.StopAsync();
        await conn.DisposeAsync();

        await Eventually.GetAsync(
            action: () => GetStatusAsync(userId),
            predicate: s => s == "offline"
        );
    }

    [Fact]
    public async Task MultiTabConnect_StaysOnline_UntilLastDisconnects()
    {
        var (token, userId) = await RegisterAsync("presenceuser3", "presence3@test.com");
        var connA = BuildConnection(token);
        var connB = BuildConnection(token);

        await connA.StartAsync();
        await connB.StartAsync();

        try
        {
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "online"
            );

            // First tab closes — second tab keeps the user online.
            await connA.StopAsync();
            await Task.Delay(300);
            (await GetStatusAsync(userId)).Should().Be("online", "the other tab is still connected");
        }
        finally
        {
            await connA.DisposeAsync();
            await connB.StopAsync();
            await connB.DisposeAsync();
        }

        await Eventually.GetAsync(
            action: () => GetStatusAsync(userId),
            predicate: s => s == "offline"
        );
    }

    [Fact]
    public async Task Heartbeat_DoesNotThrow_AndKeepsUserOnline()
    {
        var (token, userId) = await RegisterAsync("presenceuser4", "presence4@test.com");
        var conn = BuildConnection(token);

        await conn.StartAsync();
        try
        {
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "online"
            );

            var act = () => conn.InvokeAsync("Heartbeat");
            await act.Should().NotThrowAsync();

            (await GetStatusAsync(userId)).Should().Be("online");
        }
        finally
        {
            await conn.StopAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SetIdle_TogglesPublicStatusBetweenAwayAndOnline()
    {
        var (token, userId) = await RegisterAsync("presenceuser5", "presence5@test.com");
        var conn = BuildConnection(token);

        await conn.StartAsync();
        try
        {
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "online"
            );

            await conn.InvokeAsync("SetIdle", true);
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "away"
            );

            await conn.InvokeAsync("SetIdle", false);
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "online"
            );
        }
        finally
        {
            await conn.StopAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task PatchStatusInvisible_WhileConnected_MakesPublicStatusOffline()
    {
        var (token, userId) = await RegisterAsync("presenceuser6", "presence6@test.com");
        var conn = BuildConnection(token);

        await conn.StartAsync();
        try
        {
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "online"
            );

            Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await Client.PatchAsJsonAsync(
                "/api/users/me/status",
                new { status = "invisible" }
            );
            resp.EnsureSuccessStatusCode();

            // Others see offline even though the connection is still live.
            await Eventually.GetAsync(
                action: () => GetStatusAsync(userId),
                predicate: s => s == "offline"
            );
        }
        finally
        {
            await conn.StopAsync();
            await conn.DisposeAsync();
        }
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);
}
