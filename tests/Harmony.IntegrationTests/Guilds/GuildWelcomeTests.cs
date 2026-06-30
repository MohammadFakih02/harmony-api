using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// Guild welcome configuration + the member-join system message (§5.31, roadmap E#16): the
/// ManageGuild-gated PATCH (persist, channel validation, permission gating) and the live
/// "member_join" message posted through the real RabbitMQ → Scylla pipeline on invite redeem.
/// </summary>
public class GuildWelcomeTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GuildWelcomeTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<string> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<long> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Welcome Guild" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<GuildResponse>())!.Id;
    }

    // A freshly-created guild has no channels (guild-create seeds none), so make a text channel.
    private async Task<long> CreateTextChannelAsync(string token, long guildId)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "general", type = "text", position = 0 }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    [Fact]
    public async Task UpdateWelcome_PersistsConfiguration()
    {
        var token = await RegisterAsync("wel_set", "wel_set@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateTextChannelAsync(token, guildId);

        var resp = await Client.PatchAsJsonAsync(
            $"/api/guilds/{guildId}/welcome",
            new
            {
                welcomeChannelId = channelId,
                welcomeMessage = "Welcome aboard!",
                systemMessagesEnabled = true,
            }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var guild = await Client.GetFromJsonAsync<GuildResponse>($"/api/guilds/{guildId}");
        guild!.WelcomeChannelId.Should().Be(channelId);
        guild.WelcomeMessage.Should().Be("Welcome aboard!");
        guild.SystemMessagesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateWelcome_RejectsChannelFromAnotherGuild()
    {
        var token = await RegisterAsync("wel_badch", "wel_badch@test.com");
        var guildId = await CreateGuildAsync(token);
        // A channel id that doesn't belong to this guild (use a bogus snowflake).
        var resp = await Client.PatchAsJsonAsync(
            $"/api/guilds/{guildId}/welcome",
            new { welcomeChannelId = 123456789L, systemMessagesEnabled = true }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateWelcome_NonManageGuildMember_IsForbidden()
    {
        var ownerToken = await RegisterAsync("wel_owner", "wel_owner@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var code = await CreateInviteCodeAsync(guildId);

        var memberToken = await RegisterAsync("wel_member", "wel_member@test.com");
        Auth(memberToken);
        (await Client.PostAsync($"/api/invites/{code}/join", null)).EnsureSuccessStatusCode();

        var resp = await Client.PatchAsJsonAsync(
            $"/api/guilds/{guildId}/welcome",
            new { systemMessagesEnabled = false }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task JoiningGuild_PostsMemberJoinSystemMessage()
    {
        var ownerToken = await RegisterAsync("wel_jowner", "wel_jowner@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var channelId = await CreateTextChannelAsync(ownerToken, guildId);

        Auth(ownerToken);
        await Client.PatchAsJsonAsync(
            $"/api/guilds/{guildId}/welcome",
            new
            {
                welcomeChannelId = channelId,
                welcomeMessage = "Greetings traveller!",
                systemMessagesEnabled = true,
            }
        );
        var code = await CreateInviteCodeAsync(guildId);

        var joinerToken = await RegisterAsync("wel_joiner", "wel_joiner@test.com");
        Auth(joinerToken);
        (await Client.PostAsync($"/api/invites/{code}/join", null)).EnsureSuccessStatusCode();

        // The join message flows through RabbitMQ → consumer → Scylla; the owner reads it back.
        Auth(ownerToken);
        var messages = await Eventually.MatchesAsync<MessageDto>(
            async () =>
            {
                var resp = await Client.GetFromJsonAsync<ChannelMessagesResponse>(
                    $"/api/guilds/{guildId}/channels/{channelId}/messages"
                );
                return resp!.Messages;
            },
            ms => ms.Any(m => m.MessageType == "member_join")
        );

        var join = messages.Single(m => m.MessageType == "member_join");
        join.Content.Should().Be("Greetings traveller!");
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(
        long Id,
        string Name,
        long? WelcomeChannelId,
        string? WelcomeMessage,
        bool SystemMessagesEnabled
    );

    private record ChannelResponse(long Id, long? GuildId, string Name, string Type);

    private record ChannelMessagesResponse(List<MessageDto> Messages, bool Degraded);

    private record MessageDto(long MessageId, string MessageType, string Content, long UserId);
}
