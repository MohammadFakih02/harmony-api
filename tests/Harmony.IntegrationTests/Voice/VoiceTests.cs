using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Voice;

/// <summary>
/// Voice token/roster endpoint authorization (Slice 1). A guild voice channel gates on ConnectVoice
/// (a non-member is refused), a DM gates on participation, a missing channel 404s, and an authorized
/// caller gets a LiveKit token scoped to the channel room. LiveKit keys are dummy-configured in the
/// test factory, so the token is minted (and inspected) without any real LiveKit call.
/// </summary>
public class VoiceTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public VoiceTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private void Authorize(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<(string token, long userId)> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.User.Id);
    }

    private async Task<long> CreateGuildAsync(string token)
    {
        Authorize(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Voice Guild" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private async Task<long> CreateVoiceChannelAsync(string token, long guildId)
    {
        Authorize(token);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "General Voice", type = "voice", position = 0 }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    // -------------------------------------------------------------------------

    [Fact]
    public async Task Token_GuildVoiceChannel_Member_Returns200_WithChannelScopedToken()
    {
        var (owner, _) = await RegisterAsync("voice_owner1", "voice_owner1@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId);

        Authorize(owner);
        var resp = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<VoiceTokenDto>();
        body!.Token.Should().NotBeNullOrEmpty();
        body.RoomName.Should().Be(channelId.ToString());
        body.Url.Should().Be("wss://test.livekit.cloud");
    }

    [Fact]
    public async Task Token_GuildVoiceChannel_NonMember_Returns403()
    {
        var (owner, _) = await RegisterAsync("voice_owner2", "voice_owner2@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId);

        var (outsider, _) = await RegisterAsync("voice_outsider2", "voice_outsider2@test.com");
        Authorize(outsider);
        var resp = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Token_MissingChannel_Returns404()
    {
        var (owner, _) = await RegisterAsync("voice_owner3", "voice_owner3@test.com");
        Authorize(owner);

        var resp = await Client.PostAsync("/api/channels/999999999/voice/token", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Token_Dm_Participant_Returns200_NonParticipant_Returns403()
    {
        var (alice, _) = await RegisterAsync("voice_alice4", "voice_alice4@test.com");
        var (_, bobId) = await RegisterAsync("voice_bob4", "voice_bob4@test.com");
        var (carol, _) = await RegisterAsync("voice_carol4", "voice_carol4@test.com");

        Authorize(alice);
        var dmResp = await Client.PostAsJsonAsync("/api/dm", new { targetUserId = bobId });
        dmResp.EnsureSuccessStatusCode();
        var channelId = (await dmResp.Content.ReadFromJsonAsync<DmDto>())!.ChannelId;

        // Alice is a participant → 200.
        Authorize(alice);
        var ok = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // Carol is not in the DM → 403.
        Authorize(carol);
        var forbidden = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------------------------

    private record VoiceTokenDto(string Token, string Url, string RoomName);

    private record DmDto(long ChannelId);

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record IdDto(long Id);
}
