using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Core.DTOs.Responses;
using Harmony.Infrastructure.Postgres;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.IntegrationTests.Messages;

public class SendMessageFlowTests : ApiTestBase
{
    private string _token = string.Empty;
    private long _guildId;
    private long _channelId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedGuildAndChannelAsync();
    }

    // --- SendMessage ---

    [Fact]
    public async Task SendMessage_ShouldReturn200_WithValidRequest()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = "hello world" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendMessage_ShouldReturnMessageId()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = "hello world" }
        );

        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        body.Should().NotBeNull();
        body!.MessageId.Should().BeGreaterThan(0);
        body.Content.Should().Be("hello world");
        body.ChannelId.Should().Be(_channelId);
        body.GuildId.Should().Be(_guildId);
    }

    [Fact]
    public async Task SendMessage_ShouldReturn400_WhenContentEmpty()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = "" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendMessage_ShouldReturn400_WhenContentTooLong()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = new string('a', 2001) }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SendMessage_ShouldReturn404_WhenChannelNotFound()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/99999/messages",
            new { content = "hello" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendMessage_ShouldReturn401_WhenNotAuthenticated()
    {
        var freshClient = Factory.CreateClient();

        var response = await freshClient.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = "hello" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- GetMessages ---

    [Fact]
    public async Task GetMessages_ShouldReturn200()
    {
        var response = await Client.GetAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetMessages_ShouldReturnEmptyList_WhenNoMessages()
    {
        var response = await Client.GetAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages"
        );

        var body = await response.Content.ReadFromJsonAsync<List<MessageResponse>>();
        body.Should().NotBeNull();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMessages_ShouldReturn404_WhenChannelNotFound()
    {
        var response = await Client.GetAsync($"/api/guilds/{_guildId}/channels/99999/messages");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- DeleteMessage ---

    [Fact]
    public async Task DeleteMessage_ShouldReturn204()
    {
        var sendResponse = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = "to be deleted" }
        );

        var message = await sendResponse.Content.ReadFromJsonAsync<SendMessageResponse>();

        await WaitForMessageInScyllaAsync(message!.MessageId);

        var response = await Client.DeleteAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages/{message!.MessageId}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteMessage_ShouldReturn404_WhenMessageNotFound()
    {
        var response = await Client.DeleteAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages/99999"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- EditMessage ---

    [Fact]
    public async Task EditMessage_ShouldReturn204()
    {
        var sendResponse = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = "original content" }
        );

        var message = await sendResponse.Content.ReadFromJsonAsync<SendMessageResponse>();

        await WaitForMessageInScyllaAsync(message!.MessageId);

        var response = await Client.PatchAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages/{message!.MessageId}",
            new { content = "edited content" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task EditMessage_ShouldReturn404_WhenMessageNotFound()
    {
        var response = await Client.PatchAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages/99999",
            new { content = "edited" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EditMessage_ShouldReturn400_WhenContentEmpty()
    {
        var sendResponse = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages",
            new { content = "original" }
        );

        var message = await sendResponse.Content.ReadFromJsonAsync<SendMessageResponse>();

        var response = await Client.PatchAsJsonAsync(
            $"/api/guilds/{_guildId}/channels/{_channelId}/messages/{message!.MessageId}",
            new { content = "" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Helpers ---

    private async Task SeedGuildAndChannelAsync()
    {
        // Register user
        var registerResponse = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                username = "msguser",
                email = "msg@test.com",
                password = "Password123!",
            }
        );

        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponse>();

        _token = auth!.AccessToken;
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

        // Create guild
        var guildResponse = await Client.PostAsJsonAsync(
            "/api/guilds",
            new { name = "Test Guild" }
        );

        guildResponse.EnsureSuccessStatusCode();
        var guild = await guildResponse.Content.ReadFromJsonAsync<GuildIdResponse>();
        _guildId = guild!.Id;

        // Create channel
        var channelResponse = await Client.PostAsJsonAsync(
            $"/api/guilds/{_guildId}/channels",
            new { name = "general", type = "text" }
        );

        channelResponse.EnsureSuccessStatusCode();
        var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelIdResponse>();
        _channelId = channel!.Id;
    }

    private async Task WaitForMessageInScyllaAsync(long messageId)
    {
        await Eventually.GetAsync(
            action: async () =>
            {
                var response = await Client.GetAsync(
                    $"/api/guilds/{_guildId}/channels/{_channelId}/messages"
                );
                if (!response.IsSuccessStatusCode)
                    return [];
                return await response.Content.ReadFromJsonAsync<List<MessageResponse>>() ?? [];
            },
            predicate: messages => messages.Any(m => m.MessageId == messageId),
            retries: 100,
            intervalMs: 100
        );
    }

    private record AuthResponse(string AccessToken);

    private record GuildIdResponse(long Id);

    private record ChannelIdResponse(long Id);
}
