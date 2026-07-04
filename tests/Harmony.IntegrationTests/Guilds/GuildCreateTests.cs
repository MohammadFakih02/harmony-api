using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// Guild creation seeds a default #general text channel (Epic 7 — default channels): the
/// channel exists immediately, is pinned as the welcome channel, and member-join notices
/// land in it with zero welcome configuration.
/// </summary>
public class GuildCreateTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GuildCreateTests(HarmonyWebApplicationFactory factory)
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

    [Fact]
    public async Task CreateGuild_SeedsDefaultGeneralChannel_AndPinsItAsWelcomeChannel()
    {
        var token = await RegisterAsync("gc_owner", "gc_owner@test.com");
        Auth(token);

        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Fresh Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();

        var channels = await Client.GetFromJsonAsync<List<ChannelResponse>>(
            $"/api/guilds/{guild!.Id}/channels"
        );

        var general = channels.Should()
            .ContainSingle(c => c.Type == "text" && c.Name == "general")
            .Subject;
        guild.WelcomeChannelId.Should().Be(general.Id);
    }

    [Fact]
    public async Task JoiningFreshGuild_PostsMemberJoinNotice_IntoSeededGeneral()
    {
        var ownerToken = await RegisterAsync("gc_jowner", "gc_jowner@test.com");
        Auth(ownerToken);

        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Fresh Join Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        var channelId = guild!.WelcomeChannelId!.Value;

        // No welcome configuration at all — the seeded #general is the target out of the box.
        var code = await CreateInviteCodeAsync(guild.Id);

        var joinerToken = await RegisterAsync("gc_joiner", "gc_joiner@test.com");
        Auth(joinerToken);
        (await Client.PostAsync($"/api/invites/{code}/join", null)).EnsureSuccessStatusCode();

        // The join notice flows through RabbitMQ → consumer → Scylla; the owner reads it back.
        Auth(ownerToken);
        var messages = await Eventually.MatchesAsync<MessageDto>(
            async () =>
            {
                var page = await Client.GetFromJsonAsync<ChannelMessagesResponse>(
                    $"/api/guilds/{guild.Id}/channels/{channelId}/messages"
                );
                return page!.Messages;
            },
            ms => ms.Any(m => m.MessageType == "member_join")
        );

        // No admin greeting configured → plain join notice (empty content, member_join type).
        var join = messages.Single(m => m.MessageType == "member_join");
        join.Content.Should().BeEmpty();
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(long Id, string Name, long? WelcomeChannelId);

    private record ChannelResponse(long Id, long? GuildId, string Name, string Type);

    private record ChannelMessagesResponse(List<MessageDto> Messages, bool Degraded);

    private record MessageDto(long MessageId, string MessageType, string Content, long UserId);
}
