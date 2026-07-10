using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Enums;
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

        // Alice is a participant → 200, with every publish source (permissions are guild-scoped;
        // a DM participant may use camera + screenshare unconditionally).
        Authorize(alice);
        var ok = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<VoiceTokenDto>();
        PublishSourcesOf(body!.Token)
            .Should()
            .BeEquivalentTo("microphone", "camera", "screen_share", "screen_share_audio");

        // Carol is not in the DM → 403.
        Authorize(carol);
        var forbidden = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Token_Member_WithDefaultEveryone_GrantsAllPublishSources()
    {
        var (owner, _) = await RegisterAsync("voice_owner5", "voice_owner5@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId);
        var (member, _) = await RegisterAsync("voice_member5", "voice_member5@test.com");
        await JoinGuildAsync(owner, member, guildId);

        // A plain member's grant comes from @everyone: DefaultEveryone now carries
        // UseVideo AND Stream, so every source is publishable.
        Authorize(member);
        var resp = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<VoiceTokenDto>();
        PublishSourcesOf(body!.Token)
            .Should()
            .BeEquivalentTo("microphone", "camera", "screen_share", "screen_share_audio");
    }

    [Fact]
    public async Task Token_Member_WithoutVideoAndStream_GrantsMicrophoneOnly()
    {
        var (owner, _) = await RegisterAsync("voice_owner6", "voice_owner6@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId);
        var (member, _) = await RegisterAsync("voice_member6", "voice_member6@test.com");
        await JoinGuildAsync(owner, member, guildId);

        // Strip UseVideo + Stream from @everyone — the member keeps ConnectVoice (token still
        // mints) but the grant collapses to microphone only.
        Authorize(owner);
        var roles = await Client.GetFromJsonAsync<List<RoleDto>>($"/api/guilds/{guildId}/roles");
        var everyone = roles!.Single(r => r.IsDefault);
        var micOnlyBits =
            (long)Permission.DefaultEveryone
            & ~(long)Permission.UseVideo
            & ~(long)Permission.Stream;
        (
            await Client.PatchAsJsonAsync(
                $"/api/guilds/{guildId}/roles/{everyone.Id}",
                new { permissionBits = micOnlyBits }
            )
        ).EnsureSuccessStatusCode();

        Authorize(member);
        var resp = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<VoiceTokenDto>();
        PublishSourcesOf(body!.Token).Should().BeEquivalentTo("microphone");
    }

    // -------------------------------------------------------------------------

    /// <summary>Registers <paramref name="memberToken"/>'s user into the guild via a fresh invite.</summary>
    private async Task JoinGuildAsync(string ownerToken, string memberToken, long guildId)
    {
        Authorize(ownerToken);
        var create = await Client.PostAsJsonAsync($"/api/guilds/{guildId}/invites", new { });
        create.EnsureSuccessStatusCode();
        var invite = await create.Content.ReadFromJsonAsync<InviteDto>();

        Authorize(memberToken);
        (await Client.PostAsJsonAsync($"/api/invites/{invite!.Code}/join", new { }))
            .EnsureSuccessStatusCode();
    }

    /// <summary>The <c>video.canPublishSources</c> claim of a minted LiveKit JWT.</summary>
    private static string[] PublishSourcesOf(string jwt)
    {
        var segment = jwt.Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        var payload = JsonSerializer.Deserialize<JsonElement>(
            Encoding.UTF8.GetString(Convert.FromBase64String(padded))
        );
        return payload
            .GetProperty("video")
            .GetProperty("canPublishSources")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private record VoiceTokenDto(string Token, string Url, string RoomName);

    private record RoleDto(long Id, long PermissionBits, bool IsDefault);

    private record InviteDto(string Code);

    private record DmDto(long ChannelId);

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record IdDto(long Id);
}
