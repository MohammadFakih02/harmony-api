using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Hubs;

/// <summary>
/// Integration tests for ChatHub.
///
/// These tests spin up the full ASP.NET Core pipeline via HarmonyWebApplicationFactory,
/// connect a real SignalR HubConnection, and assert hub behaviour end-to-end.
///
/// The in-process backplane is used (Redis connection string is empty in test config),
/// so all connections share the same process and group routing works without Redis.
/// </summary>
public class ChatHubTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public ChatHubTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a SignalR HubConnection that authenticates with the given JWT
    /// and connects to /hubs/chat via the test server's HTTP handler.
    /// </summary>
    private HubConnection BuildConnection(string accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(
                // Factory.Server.BaseAddress gives us the in-process test server URL.
                // We append the access_token query string so the JWT bearer event
                // in Program.cs injects it as the bearer credential for WebSocket upgrades.
                new Uri(Factory.Server.BaseAddress, $"hubs/chat?access_token={accessToken}"),
                options =>
                {
                    // Use the test server's HttpMessageHandler so SignalR goes through
                    // the in-process pipeline rather than over a real network socket.
                    options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                }
            )
            // The server serializes every Snowflake long as a JSON string (LongStringConverter);
            // mirror that on the client so string ids deserialize back into long DTO fields.
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString
            )
            .Build();
    }

    /// <summary>Registers a user and returns their access token.</summary>
    private async Task<string> RegisterAndGetTokenAsync(
        string username,
        string email,
        string password = "Password123!"
    )
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username,
                email,
                password,
            }
        );
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.AccessToken;
    }

    /// <summary>Creates a guild and returns its ID.</summary>
    private async Task<long> CreateGuildAsync(string token, string name = "Test Guild")
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await Client.PostAsJsonAsync("/api/guilds", new { name });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    /// <summary>Creates a channel and returns its ID.</summary>
    private async Task<long> CreateChannelAsync(string token, long guildId, string name = "general")
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name, type = "text" }
        );
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<IdResponse>();
        return body!.Id;
    }

    /// <summary>Registers a user and returns their access token AND user id.</summary>
    private async Task<(string token, long userId)> RegisterWithIdAsync(string username, string email)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponseWithUser>();
        return (body!.AccessToken, body.User.Id);
    }

    /// <summary>Opens (or reuses) a 1:1 DM with the target user and returns its channel id.</summary>
    private async Task<long> CreateDmAsync(string token, long targetUserId)
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await Client.PostAsJsonAsync("/api/dm", new { targetUserId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DmChannelDto>();
        return body!.ChannelId;
    }

    // -------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Hub_ShouldRejectConnection_WhenNoTokenProvided()
    {
        // A connection with no access_token should be rejected with 401.
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, "hubs/chat"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                }
            )
            .Build();

        var act = async () => await connection.StartAsync();

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Hub_ShouldRejectConnection_WhenTokenIsInvalid()
    {
        var connection = BuildConnection("this.is.not.a.valid.jwt");

        var act = async () => await connection.StartAsync();

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Hub_ShouldAcceptConnection_WhenTokenIsValid()
    {
        var token = await RegisterAndGetTokenAsync("hubuser1", "hubuser1@test.com");
        var connection = BuildConnection(token);

        try
        {
            await connection.StartAsync();
            connection.State.Should().Be(HubConnectionState.Connected);
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Group management
    // -------------------------------------------------------------------------

    [Fact]
    public async Task JoinChannel_ShouldComplete_WithoutError()
    {
        var token = await RegisterAndGetTokenAsync("hubuser2", "hubuser2@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            // Should not throw
            var act = async () => await connection.InvokeAsync("JoinChannel", channelId);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task LeaveChannel_ShouldComplete_WithoutError()
    {
        var token = await RegisterAndGetTokenAsync("hubuser3", "hubuser3@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            await connection.InvokeAsync("JoinChannel", channelId);
            var act = async () => await connection.InvokeAsync("LeaveChannel", channelId);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task JoinGuild_ShouldComplete_WithoutError()
    {
        var token = await RegisterAndGetTokenAsync("hubuser4", "hubuser4@test.com");
        var guildId = await CreateGuildAsync(token);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            var act = async () => await connection.InvokeAsync("JoinGuild", guildId);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task LeaveGuild_ShouldComplete_WithoutError()
    {
        var token = await RegisterAndGetTokenAsync("hubuser5", "hubuser5@test.com");
        var guildId = await CreateGuildAsync(token);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            await connection.InvokeAsync("JoinGuild", guildId);
            var act = async () => await connection.InvokeAsync("LeaveGuild", guildId);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------
    // SendMessage validation (Result Pattern)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendMessage_ShouldReturnSuccessfulResult_WithValidInput()
    {
        var token = await RegisterAndGetTokenAsync("hubuser6", "hubuser6@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            // Every optional parameter must be passed explicitly: SignalR binds hub arguments
            // positionally and rejects a count mismatch outright ("Invocation provides 3
            // argument(s) but target expects 6"), before the method body ever runs — C# default
            // values do not make a hub parameter optional over the wire. The real client does the
            // same (harmony-hub.client.ts sends attachmentIds/replyToId/nonce as explicit nulls).
            var result = await connection.InvokeAsync<HubResultDto<SendMessageResponseDto>>(
                "SendMessage",
                channelId,
                guildId,
                "hello from hub",
                null,
                null,
                null
            );

            result.Should().NotBeNull();
            result.Succeeded.Should().BeTrue("valid input should succeed");
            result.ErrorMessage.Should().BeNull();
            result.Data.Should().NotBeNull();
            result.Data!.MessageId.Should().BeGreaterThan(0);
            result.Data.Content.Should().Be("hello from hub");
            result.Data.ChannelId.Should().Be(channelId);
            result.Data.GuildId.Should().Be(guildId);
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendMessage_ShouldReturnFailedResult_WhenContentIsEmpty()
    {
        var token = await RegisterAndGetTokenAsync("hubuser7", "hubuser7@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            var result = await connection.InvokeAsync<HubResultDto<SendMessageResponseDto>>(
                "SendMessage",
                channelId,
                guildId,
                "",
                null,
                null,
                null
            );

            result.Should().NotBeNull();
            result.Succeeded.Should().BeFalse("empty content should fail");
            result.Data.Should().BeNull();
            result.ErrorMessage.Should().Contain("between 1 and 2000 characters");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendMessage_ShouldReturnFailedResult_WhenContentExceeds2000Chars()
    {
        var token = await RegisterAndGetTokenAsync("hubuser8", "hubuser8@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            var result = await connection.InvokeAsync<HubResultDto<SendMessageResponseDto>>(
                "SendMessage",
                channelId,
                guildId,
                new string('x', 2001),
                null,
                null,
                null
            );

            result.Should().NotBeNull();
            result.Succeeded.Should().BeFalse("oversized content should fail");
            result.Data.Should().BeNull();
            result.ErrorMessage.Should().Contain("between 1 and 2000 characters");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendMessage_ShouldReturnFailedResult_WhenChannelNotFound()
    {
        var token = await RegisterAndGetTokenAsync("hubuser9", "hubuser9@test.com");
        var guildId = await CreateGuildAsync(token);
        var connection = BuildConnection(token);

        await connection.StartAsync();
        try
        {
            var result = await connection.InvokeAsync<HubResultDto<SendMessageResponseDto>>(
                "SendMessage",
                999999L,
                guildId,
                "hello",
                null,
                null,
                null
            );

            result.Should().NotBeNull();
            result.Succeeded.Should().BeFalse("non-existent channel should fail");
            result.Data.Should().BeNull();
            result.ErrorMessage.Should().Contain("not found");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendMessage_ShouldSucceed_ForDm_WithNullGuildId()
    {
        // The hub now accepts a null guildId → DM/group-DM send (parity with the REST DM endpoint).
        var senderToken = await RegisterAndGetTokenAsync("hubdm_a", "hubdm_a@test.com");
        var (_, peerId) = await RegisterWithIdAsync("hubdm_b", "hubdm_b@test.com");
        var channelId = await CreateDmAsync(senderToken, peerId);
        var connection = BuildConnection(senderToken);

        await connection.StartAsync();
        try
        {
            var result = await connection.InvokeAsync<HubResultDto<SendMessageDmResponseDto>>(
                "SendMessage",
                channelId,
                null,
                "hello over the dm hub",
                null,
                null,
                null
            );

            result.Should().NotBeNull();
            result.Succeeded.Should().BeTrue("a participant can DM through the hub");
            result.ErrorMessage.Should().BeNull();
            result.Data.Should().NotBeNull();
            result.Data!.MessageId.Should().BeGreaterThan(0);
            result.Data.ChannelId.Should().Be(channelId);
            result.Data.GuildId.Should().BeNull();
            result.Data.Content.Should().Be("hello over the dm hub");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------
    // DTOs local to this test file
    // -------------------------------------------------------------------------

    private record HubResultDto<T>(bool Succeeded, T? Data, string? ErrorMessage);

    private record AuthResponse(string AccessToken);

    private record AuthResponseWithUser(string AccessToken, UserIdDto User);

    private record UserIdDto(long Id);

    private record DmChannelDto(long ChannelId);

    private record IdResponse(long Id);

    private record SendMessageResponseDto(
        long MessageId,
        long ChannelId,
        long GuildId,
        long UserId,
        string Content,
        string MessageType,
        long SentAt
    );

    private record SendMessageDmResponseDto(
        long MessageId,
        long ChannelId,
        long? GuildId,
        long UserId,
        string Content,
        string MessageType,
        long SentAt
    );
}
