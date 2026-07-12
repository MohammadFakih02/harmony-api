using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Hubs;

/// <summary>
/// Voice moderation (Slice B1): ModerateVoiceState / MoveVoiceParticipant over a real
/// two-connection SignalR session against real Redis voice state, plus the UserLimit gates
/// (hub JoinVoice + the REST token mirror). The LiveKit hard-enforcement arm is a no-op stub in
/// tests (see HarmonyWebApplicationFactory) — these assert the soft layer: Redis flags,
/// permission gates, and the broadcasts/targeted events honest clients act on.
/// </summary>
public class VoiceModerationTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    public VoiceModerationTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Mod Guild" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private async Task<long> CreateVoiceChannelAsync(
        string token,
        long guildId,
        string name = "Voice",
        int? userLimit = null
    )
    {
        Authorize(token);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name, type = "voice", position = 0, userLimit }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

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

    private HubConnection BuildConnection(string accessToken)
    {
        return new HubConnectionBuilder()
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
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> tcs, string what)
    {
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(EventTimeout));
        winner.Should().Be(tcs.Task, $"expected {what} within {EventTimeout.TotalSeconds}s");
        return await tcs.Task;
    }

    // -------------------------------------------------------------------------
    // ModerateVoiceState
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ModerateVoiceState_Owner_ServerMutesMember_MemberSeesUpdate()
    {
        var (owner, _) = await RegisterAsync("vmod_owner1", "vmod_owner1@test.com");
        var (member, memberId) = await RegisterAsync("vmod_member1", "vmod_member1@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId);
        await JoinGuildAsync(owner, member, guildId);

        await using var memberConn = BuildConnection(member);
        await using var ownerConn = BuildConnection(owner);
        await memberConn.StartAsync();
        await ownerConn.StartAsync();

        var updated = new TaskCompletionSource<VoiceStateDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        memberConn.On<VoiceStateDto>("VoiceStateUpdated", p =>
        {
            if (p.UserId == memberId && p.IsServerMuted)
                updated.TrySetResult(p);
        });

        await memberConn.InvokeAsync("JoinGuild", guildId);
        await memberConn.InvokeAsync("JoinVoice", channelId);

        await ownerConn.InvokeAsync("ModerateVoiceState", memberId, true, (bool?)null);

        var payload = await WaitAsync(updated, "VoiceStateUpdated with IsServerMuted");
        payload.ChannelId.Should().Be(channelId);
        payload.IsServerMuted.Should().BeTrue();
        payload.IsServerDeafened.Should().BeFalse();
    }

    [Fact]
    public async Task ModerateVoiceState_PlainMember_IsDenied()
    {
        var (owner, ownerId) = await RegisterAsync("vmod_owner2", "vmod_owner2@test.com");
        var (member, _) = await RegisterAsync("vmod_member2", "vmod_member2@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId);
        await JoinGuildAsync(owner, member, guildId);

        await using var ownerConn = BuildConnection(owner);
        await using var memberConn = BuildConnection(member);
        await ownerConn.StartAsync();
        await memberConn.StartAsync();

        await ownerConn.InvokeAsync("JoinVoice", channelId);

        // DefaultEveryone carries no MuteMembers/DeafenMembers — a plain member cannot moderate.
        var muteAttempt = () => memberConn.InvokeAsync("ModerateVoiceState", ownerId, true, (bool?)null);
        (await muteAttempt.Should().ThrowAsync<HubException>())
            .WithMessage("*permission to server mute*");

        var deafenAttempt = () =>
            memberConn.InvokeAsync("ModerateVoiceState", ownerId, (bool?)null, true);
        (await deafenAttempt.Should().ThrowAsync<HubException>())
            .WithMessage("*permission to server deafen*");
    }

    [Fact]
    public async Task ModerateVoiceState_TargetNotInVoice_Throws()
    {
        var (owner, _) = await RegisterAsync("vmod_owner3", "vmod_owner3@test.com");
        var (member, memberId) = await RegisterAsync("vmod_member3", "vmod_member3@test.com");
        var guildId = await CreateGuildAsync(owner);
        await CreateVoiceChannelAsync(owner, guildId);
        await JoinGuildAsync(owner, member, guildId);

        await using var ownerConn = BuildConnection(owner);
        await ownerConn.StartAsync();

        var attempt = () => ownerConn.InvokeAsync("ModerateVoiceState", memberId, true, (bool?)null);
        (await attempt.Should().ThrowAsync<HubException>()).WithMessage("*not in a voice channel*");
    }

    // -------------------------------------------------------------------------
    // MoveVoiceParticipant
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MoveVoiceParticipant_Owner_MovesMember_TargetGetsForceMoved()
    {
        var (owner, _) = await RegisterAsync("vmod_owner4", "vmod_owner4@test.com");
        var (member, memberId) = await RegisterAsync("vmod_member4", "vmod_member4@test.com");
        var guildId = await CreateGuildAsync(owner);
        var roomA = await CreateVoiceChannelAsync(owner, guildId, "Voice A");
        var roomB = await CreateVoiceChannelAsync(owner, guildId, "Voice B");
        await JoinGuildAsync(owner, member, guildId);

        await using var memberConn = BuildConnection(member);
        await using var ownerConn = BuildConnection(owner);
        await memberConn.StartAsync();
        await ownerConn.StartAsync();

        var forceMoved = new TaskCompletionSource<ForceMovedDto>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        memberConn.On<ForceMovedDto>("VoiceForceMoved", p => forceMoved.TrySetResult(p));

        await memberConn.InvokeAsync("JoinVoice", roomA);
        await ownerConn.InvokeAsync("MoveVoiceParticipant", memberId, roomB);

        var payload = await WaitAsync(forceMoved, "VoiceForceMoved");
        payload.FromChannelId.Should().Be(roomA);
        payload.ToChannelId.Should().Be(roomB);
        payload.GuildId.Should().Be(guildId);

        // The roster is authoritative server-side — the member is already in room B.
        Authorize(owner);
        var roster = await Client.GetFromJsonAsync<List<VoiceStateDto>>(
            $"/api/channels/{roomB}/voice/participants"
        );
        roster!.Should().ContainSingle(p => p.UserId == memberId);
    }

    [Fact]
    public async Task MoveVoiceParticipant_PlainMember_IsDenied()
    {
        var (owner, ownerId) = await RegisterAsync("vmod_owner5", "vmod_owner5@test.com");
        var (member, _) = await RegisterAsync("vmod_member5", "vmod_member5@test.com");
        var guildId = await CreateGuildAsync(owner);
        var roomA = await CreateVoiceChannelAsync(owner, guildId, "Voice A");
        var roomB = await CreateVoiceChannelAsync(owner, guildId, "Voice B");
        await JoinGuildAsync(owner, member, guildId);

        await using var ownerConn = BuildConnection(owner);
        await using var memberConn = BuildConnection(member);
        await ownerConn.StartAsync();
        await memberConn.StartAsync();

        await ownerConn.InvokeAsync("JoinVoice", roomA);

        var attempt = () => memberConn.InvokeAsync("MoveVoiceParticipant", ownerId, roomB);
        (await attempt.Should().ThrowAsync<HubException>()).WithMessage("*permission to move*");
    }

    // -------------------------------------------------------------------------
    // UserLimit
    // -------------------------------------------------------------------------

    [Fact]
    public async Task JoinVoice_FullChannel_RejectsPlainMember_ButOwnerBypasses()
    {
        var (owner, _) = await RegisterAsync("vmod_owner6", "vmod_owner6@test.com");
        var (first, _) = await RegisterAsync("vmod_first6", "vmod_first6@test.com");
        var (second, _) = await RegisterAsync("vmod_second6", "vmod_second6@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId, "Tight Voice", userLimit: 1);
        await JoinGuildAsync(owner, first, guildId);
        await JoinGuildAsync(owner, second, guildId);

        await using var firstConn = BuildConnection(first);
        await using var secondConn = BuildConnection(second);
        await using var ownerConn = BuildConnection(owner);
        await firstConn.StartAsync();
        await secondConn.StartAsync();
        await ownerConn.StartAsync();

        await firstConn.InvokeAsync("JoinVoice", channelId);

        // Channel is at its 1-user cap: a plain member bounces off…
        var attempt = () => secondConn.InvokeAsync("JoinVoice", channelId);
        (await attempt.Should().ThrowAsync<HubException>()).WithMessage("*full*");

        // …the REST token mirror refuses too…
        Authorize(second);
        var tokenResp = await Client.PostAsync($"/api/channels/{channelId}/voice/token", null);
        tokenResp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // …but the owner (MoveMembers) bypasses the cap, and a reconnect of someone already
        // inside is never blocked.
        await ownerConn.InvokeAsync("JoinVoice", channelId);
        await firstConn.InvokeAsync("JoinVoice", channelId);
    }

    [Fact]
    public async Task UpdateChannel_UserLimitZero_ClearsTheLimit()
    {
        var (owner, _) = await RegisterAsync("vmod_owner7", "vmod_owner7@test.com");
        var guildId = await CreateGuildAsync(owner);
        var channelId = await CreateVoiceChannelAsync(owner, guildId, "Limited", userLimit: 5);

        Authorize(owner);
        var patch = await Client.PatchAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}",
            new { userLimit = 0 }
        );
        patch.EnsureSuccessStatusCode();

        var channel = await patch.Content.ReadFromJsonAsync<ChannelDto>();
        channel!.UserLimit.Should().BeNull("0 clears the limit back to unlimited");
    }

    // -------------------------------------------------------------------------

    private record VoiceStateDto(
        long ChannelId,
        long? GuildId,
        long UserId,
        bool IsMuted,
        bool IsDeafened,
        bool IsVideoOn,
        bool IsStreaming,
        bool IsServerMuted,
        bool IsServerDeafened
    );

    private record ForceMovedDto(long FromChannelId, long ToChannelId, long? GuildId);

    private record ChannelDto(long Id, int? Bitrate, int? UserLimit);

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record IdDto(long Id);

    private record InviteDto(string Code);
}
