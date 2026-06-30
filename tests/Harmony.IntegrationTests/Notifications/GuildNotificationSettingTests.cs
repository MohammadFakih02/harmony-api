using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Notifications;

/// <summary>
/// The caller's per-guild / per-channel notification levels (§5.31, roadmap E#16) against real
/// Postgres: guild- and channel-scope upsert, the GET projection (resolved guild level + explicit
/// channel overrides), reset-to-default (DELETE), level validation, and member gating.
/// </summary>
public class GuildNotificationSettingTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GuildNotificationSettingTests(HarmonyWebApplicationFactory factory)
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
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Notif Guild" });
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
    public async Task DefaultsToMentions_WhenNothingSet()
    {
        var token = await RegisterAsync("ns_def", "ns_def@test.com");
        var guildId = await CreateGuildAsync(token);

        var settings = await Client.GetFromJsonAsync<GuildNotificationSettingsResponse>(
            $"/api/guilds/{guildId}/notification-settings"
        );
        settings!.GuildLevel.Should().Be("mentions");
        settings.Channels.Should().BeEmpty();
    }

    [Fact]
    public async Task SetGuildLevel_AndChannelOverride_AreReflectedInGet()
    {
        var token = await RegisterAsync("ns_set", "ns_set@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateTextChannelAsync(token, guildId);

        (await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/notification-settings",
            new { level = "nothing" }
        )).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/notification-settings",
            new { level = "all" }
        )).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var settings = await Client.GetFromJsonAsync<GuildNotificationSettingsResponse>(
            $"/api/guilds/{guildId}/notification-settings"
        );
        settings!.GuildLevel.Should().Be("nothing");
        settings.Channels.Should().ContainSingle(c => c.ChannelId == channelId && c.Level == "all");
    }

    [Fact]
    public async Task ResetChannel_RemovesTheOverride()
    {
        var token = await RegisterAsync("ns_reset", "ns_reset@test.com");
        var guildId = await CreateGuildAsync(token);
        var channelId = await CreateTextChannelAsync(token, guildId);

        await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/notification-settings",
            new { level = "nothing" }
        );
        (await Client.DeleteAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/notification-settings"
        )).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var settings = await Client.GetFromJsonAsync<GuildNotificationSettingsResponse>(
            $"/api/guilds/{guildId}/notification-settings"
        );
        settings!.Channels.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidLevel_IsRejected()
    {
        var token = await RegisterAsync("ns_bad", "ns_bad@test.com");
        var guildId = await CreateGuildAsync(token);

        var resp = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/notification-settings",
            new { level = "loud" }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NonMember_IsForbidden()
    {
        var ownerToken = await RegisterAsync("ns_owner", "ns_owner@test.com");
        var guildId = await CreateGuildAsync(ownerToken);

        var outsider = await RegisterAsync("ns_outsider", "ns_outsider@test.com");
        Auth(outsider);
        var resp = await Client.GetAsync($"/api/guilds/{guildId}/notification-settings");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(long Id, string Name);

    private record ChannelResponse(long Id, long? GuildId, string Name, string Type);

    private record GuildNotificationSettingsResponse(
        string GuildLevel,
        List<ChannelNotificationSettingResponse> Channels
    );

    private record ChannelNotificationSettingResponse(long ChannelId, string Level);
}
