using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.Application.DTOs.Responses;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Hubs;

/// <summary>
/// End-to-end broadcast flow tests.
///
/// These tests verify the full async pipeline:
///   REST POST /messages → RabbitMQ publish → ScyllaMessageConsumer
///   → ScyllaDB persist → IHubContext broadcast → SignalR client receives MessageReceived
///
/// All connections share the same in-process test server, so group routing
/// works correctly without Redis.
///
/// Timing: The pipeline is async (RabbitMQ → consumer → hub), so we use
/// Eventually helpers with a generous timeout to wait for the broadcast.
/// </summary>
public class HubBroadcastFlowTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public HubBroadcastFlowTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private HubConnection BuildConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, $"hubs/chat?access_token={accessToken}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                }
            )
            // The server serializes every Snowflake long as a JSON string (LongStringConverter);
            // mirror that on the client so string ids deserialize back into long DTO fields.
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString
            )
            .Build();

    private async Task<string> RegisterAndGetTokenAsync(string username, string email)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username,
                email,
                password = "Password123!",
            }
        );
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.AccessToken;
    }

    private async Task<(long guildId, long channelId)> SetupGuildAndChannelAsync(string token)
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var guildResp = await Client.PostAsJsonAsync(
            "/api/guilds",
            new { name = "Broadcast Guild" }
        );
        guildResp.EnsureSuccessStatusCode();
        var guild = await guildResp.Content.ReadFromJsonAsync<IdResponse>();

        var channelResp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild!.Id}/channels",
            new { name = "general", type = "text" }
        );
        channelResp.EnsureSuccessStatusCode();
        var channel = await channelResp.Content.ReadFromJsonAsync<IdResponse>();

        return (guild.Id, channel!.Id);
    }

    // -------------------------------------------------------------------------
    // MessageReceived broadcast
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendMessage_ViaRest_ShouldBroadcastMessageReceived_ToChannelSubscribers()
    {
        // Arrange
        var token = await RegisterAndGetTokenAsync("broadcastuser1", "broadcast1@test.com");
        var (guildId, channelId) = await SetupGuildAndChannelAsync(token);

        var connection = BuildConnection(token);
        var received = new List<MessageResponse>();
        connection.On<MessageResponse>("MessageReceived", msg => received.Add(msg));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", channelId);

        try
        {
            // Act — send via REST (goes through RabbitMQ → consumer → hub)
            Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            await Client.PostAsJsonAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/messages",
                new { content = "hello broadcast" }
            );

            // Assert — poll until the hub fires MessageReceived
            await Eventually.GetAsync(
                action: () => Task.FromResult(received),
                predicate: msgs => msgs.Any(m => m.Content == "hello broadcast"),
                retries: 100,
                intervalMs: 100
            );

            received
                .Should()
                .ContainSingle(m =>
                    m.Content == "hello broadcast"
                    && m.ChannelId == channelId
                    && m.GuildId == guildId
                    && m.IsDeleted == false
                    && m.MessageId > 0
                );
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendMessage_ViaHub_ShouldBroadcastMessageReceived_ToAllChannelSubscribers()
    {
        // Arrange — two separate connections subscribed to the same channel
        var senderToken = await RegisterAndGetTokenAsync("sender1", "sender1@test.com");
        var (guildId, channelId) = await SetupGuildAndChannelAsync(senderToken);

        // Second user joins the guild. Resolve the invite code FIRST (it sets the auth
        // header to senderToken), THEN switch the header to the receiver — otherwise the
        // join POST runs as the owner and the receiver is never added as a member.
        var receiverToken = await RegisterAndGetTokenAsync("receiver1", "receiver1@test.com");
        var inviteCode = await GetInviteCodeAsync(senderToken, guildId);
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", receiverToken);
        var joinResponse = await Client.PostAsJsonAsync(
            $"/api/invites/{inviteCode}/join",
            new { }
        );
        joinResponse.EnsureSuccessStatusCode();

        var senderConn = BuildConnection(senderToken);
        var receiverConn = BuildConnection(receiverToken);

        var senderReceived = new List<MessageResponse>();
        var receiverReceived = new List<MessageResponse>();

        senderConn.On<MessageResponse>("MessageReceived", msg => senderReceived.Add(msg));
        receiverConn.On<MessageResponse>("MessageReceived", msg => receiverReceived.Add(msg));

        await senderConn.StartAsync();
        await receiverConn.StartAsync();

        await senderConn.InvokeAsync("JoinChannel", channelId);
        await receiverConn.InvokeAsync("JoinChannel", channelId);

        try
        {
            // Act — send via hub
            await senderConn.InvokeAsync("SendMessage", channelId, guildId, "hub message");

            // Assert — BOTH connections receive MessageReceived (including sender)
            await Eventually.GetAsync(
                action: () => Task.FromResult(senderReceived),
                predicate: msgs => msgs.Any(m => m.Content == "hub message"),
                retries: 100,
                intervalMs: 100
            );

            await Eventually.GetAsync(
                action: () => Task.FromResult(receiverReceived),
                predicate: msgs => msgs.Any(m => m.Content == "hub message"),
                retries: 100,
                intervalMs: 100
            );

            senderReceived.Should().ContainSingle(m => m.Content == "hub message");
            receiverReceived.Should().ContainSingle(m => m.Content == "hub message");
        }
        finally
        {
            await senderConn.StopAsync();
            await receiverConn.StopAsync();
            await senderConn.DisposeAsync();
            await receiverConn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendMessage_ShouldNotDeliverMessageReceived_ToConnectionsNotInChannelGroup()
    {
        // Arrange
        var token = await RegisterAndGetTokenAsync("isolated1", "isolated1@test.com");
        var (guildId, channelId) = await SetupGuildAndChannelAsync(token);

        var subscribedConn = BuildConnection(token);
        var unsubscribedConn = BuildConnection(token);

        var subscribedReceived = new List<MessageResponse>();
        var unsubscribedReceived = new List<MessageResponse>();

        subscribedConn.On<MessageResponse>("MessageReceived", msg => subscribedReceived.Add(msg));
        unsubscribedConn.On<MessageResponse>(
            "MessageReceived",
            msg => unsubscribedReceived.Add(msg)
        );

        await subscribedConn.StartAsync();
        await unsubscribedConn.StartAsync();

        // Only subscribed connection joins the channel group
        await subscribedConn.InvokeAsync("JoinChannel", channelId);
        // unsubscribedConn deliberately does NOT call JoinChannel

        try
        {
            Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            await Client.PostAsJsonAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/messages",
                new { content = "targeted message" }
            );

            // Wait for the subscribed connection to receive it
            await Eventually.GetAsync(
                action: () => Task.FromResult(subscribedReceived),
                predicate: msgs => msgs.Any(m => m.Content == "targeted message"),
                retries: 100,
                intervalMs: 100
            );

            // Assert unsubscribed connection received nothing
            subscribedReceived.Should().ContainSingle(m => m.Content == "targeted message");
            unsubscribedReceived.Should().BeEmpty();
        }
        finally
        {
            await subscribedConn.StopAsync();
            await unsubscribedConn.StopAsync();
            await subscribedConn.DisposeAsync();
            await unsubscribedConn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LeaveChannel_ShouldStopReceivingMessages_AfterLeaving()
    {
        // Arrange
        var token = await RegisterAndGetTokenAsync("leaveuser1", "leaveuser1@test.com");
        var (guildId, channelId) = await SetupGuildAndChannelAsync(token);

        var connection = BuildConnection(token);
        var received = new List<MessageResponse>();
        connection.On<MessageResponse>("MessageReceived", msg => received.Add(msg));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", channelId);

        // Send one message while subscribed — assert it arrives
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content = "before leave" }
        );

        await Eventually.GetAsync(
            action: () => Task.FromResult(received),
            predicate: msgs => msgs.Any(m => m.Content == "before leave"),
            retries: 100,
            intervalMs: 100
        );

        try
        {
            // Act — leave the channel group
            await connection.InvokeAsync("LeaveChannel", channelId);

            var countBeforeSecondMessage = received.Count;

            // Send a second message — should NOT arrive
            await Client.PostAsJsonAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/messages",
                new { content = "after leave" }
            );

            // Wait a reasonable time and assert the second message was NOT received
            await Task.Delay(1000);

            received
                .Count.Should()
                .Be(
                    countBeforeSecondMessage,
                    because: "connection left the channel group before the second message was sent"
                );
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<string> GetInviteCodeAsync(string token, long guildId)
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await CreateInviteCodeAsync(guildId);
    }

    private record AuthResponse(string AccessToken);

    private record IdResponse(long Id);

    private record GuildResponse(long Id, string Name);
}
