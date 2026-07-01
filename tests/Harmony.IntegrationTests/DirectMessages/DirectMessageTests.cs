using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.DirectMessages;

/// <summary>
/// REST-side tests for direct-messages: open/reuse a 1:1 DM, list and hide it, the
/// reappear-on-new-message rule, participant + block authorization, and a full
/// send → history round-trip proving a guild-less (guildId null) message threads
/// through the same persist → broadcast pipeline.
/// </summary>
public class DirectMessageTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public DirectMessageTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

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

    private void Authorize(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private Task<HttpResponseMessage> OpenDmAsync(long targetUserId) =>
        Client.PostAsJsonAsync("/api/dm", new { targetUserId });

    [Fact]
    public async Task CreateDm_ReturnsChannel_AndBothParticipantsSeeIt()
    {
        var (tokenA, _) = await RegisterAsync("dm_a1", "dm_a1@test.com");
        var (tokenB, idB) = await RegisterAsync("dm_b1", "dm_b1@test.com");

        Authorize(tokenA);
        var create = await OpenDmAsync(idB);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var dm = await create.Content.ReadFromJsonAsync<DmDto>();
        dm!.IsGroup.Should().BeFalse();
        dm.Participants.Should().ContainSingle(p => p.UserId == idB);

        var aList = await Client.GetFromJsonAsync<List<DmDto>>("/api/dm");
        aList.Should().ContainSingle(d =>
            d.ChannelId == dm.ChannelId && d.Participants.Any(p => p.UserId == idB)
        );

        Authorize(tokenB);
        var bList = await Client.GetFromJsonAsync<List<DmDto>>("/api/dm");
        bList.Should().ContainSingle(d => d.ChannelId == dm.ChannelId);
    }

    [Fact]
    public async Task CreateDm_IsIdempotent_ReturnsSameChannel()
    {
        var (tokenA, _) = await RegisterAsync("dm_a2", "dm_a2@test.com");
        var (_, idB) = await RegisterAsync("dm_b2", "dm_b2@test.com");
        Authorize(tokenA);

        var first = await (await OpenDmAsync(idB)).Content.ReadFromJsonAsync<DmDto>();
        var second = await (await OpenDmAsync(idB)).Content.ReadFromJsonAsync<DmDto>();

        second!.ChannelId.Should().Be(first!.ChannelId);
        (await Client.GetFromJsonAsync<List<DmDto>>("/api/dm")).Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateDm_WithSelf_Returns400()
    {
        var (tokenA, idA) = await RegisterAsync("dm_a3", "dm_a3@test.com");
        Authorize(tokenA);

        (await OpenDmAsync(idA)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateDm_WithUnknownUser_Returns404()
    {
        var (tokenA, _) = await RegisterAsync("dm_a4", "dm_a4@test.com");
        Authorize(tokenA);

        (await OpenDmAsync(999999999999)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateDm_WhenBlocked_Returns403()
    {
        var (tokenA, _) = await RegisterAsync("dm_a5", "dm_a5@test.com");
        var (_, idB) = await RegisterAsync("dm_b5", "dm_b5@test.com");

        Authorize(tokenA);
        (await Client.PostAsync($"/api/users/{idB}/block", null)).EnsureSuccessStatusCode();

        (await OpenDmAsync(idB)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hide_RemovesFromList_AndNewMessageResurfacesIt()
    {
        var (tokenA, _) = await RegisterAsync("dm_a6", "dm_a6@test.com");
        var (tokenB, idB) = await RegisterAsync("dm_b6", "dm_b6@test.com");

        Authorize(tokenA);
        var dm = await (await OpenDmAsync(idB)).Content.ReadFromJsonAsync<DmDto>();

        // B hides the DM → it leaves B's list.
        Authorize(tokenB);
        (await Client.PatchAsync($"/api/dm/{dm!.ChannelId}/hide", null)).EnsureSuccessStatusCode();
        (await Client.GetFromJsonAsync<List<DmDto>>("/api/dm")).Should().BeEmpty();

        // A sends a message → the DM resurfaces for B (unhide is synchronous on send).
        Authorize(tokenA);
        (await SendDmAsync(dm.ChannelId, "you there?")).EnsureSuccessStatusCode();

        Authorize(tokenB);
        (await Client.GetFromJsonAsync<List<DmDto>>("/api/dm"))
            .Should()
            .ContainSingle(d => d.ChannelId == dm.ChannelId);
    }

    [Fact]
    public async Task SendAndGet_DmMessage_RoundTrips_WithNullGuildId()
    {
        var (tokenA, _) = await RegisterAsync("dm_a7", "dm_a7@test.com");
        var (tokenB, idB) = await RegisterAsync("dm_b7", "dm_b7@test.com");

        Authorize(tokenA);
        var dm = await (await OpenDmAsync(idB)).Content.ReadFromJsonAsync<DmDto>();

        var send = await SendDmAsync(dm!.ChannelId, "hello over dm");
        send.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = await send.Content.ReadFromJsonAsync<SendDto>();
        sent!.GuildId.Should().BeNull("a DM message has no guild");

        // The recipient can read it once the consumer has persisted it.
        Authorize(tokenB);
        await Eventually.GetAsync(
            action: async () =>
            {
                var resp = await Client.GetAsync($"/api/dm/{dm.ChannelId}/messages");
                if (!resp.IsSuccessStatusCode)
                    return [];
                var body = await resp.Content.ReadFromJsonAsync<MessagesDto>();
                return body?.Messages.ToList() ?? [];
            },
            predicate: msgs => msgs.Any(m => m.MessageId == sent.MessageId && m.Content == "hello over dm"),
            retries: 100,
            intervalMs: 100
        );
    }

    [Fact]
    public async Task Send_ByNonParticipant_Returns403()
    {
        var (tokenA, _) = await RegisterAsync("dm_a8", "dm_a8@test.com");
        var (_, idB) = await RegisterAsync("dm_b8", "dm_b8@test.com");
        var (tokenC, _) = await RegisterAsync("dm_c8", "dm_c8@test.com");

        Authorize(tokenA);
        var dm = await (await OpenDmAsync(idB)).Content.ReadFromJsonAsync<DmDto>();

        // A third user who is not a participant cannot send into the DM.
        Authorize(tokenC);
        (await SendDmAsync(dm!.ChannelId, "intruder")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetMessages_ByNonParticipant_Returns403()
    {
        var (tokenA, _) = await RegisterAsync("dm_a9", "dm_a9@test.com");
        var (_, idB) = await RegisterAsync("dm_b9", "dm_b9@test.com");
        var (tokenC, _) = await RegisterAsync("dm_c9", "dm_c9@test.com");

        Authorize(tokenA);
        var dm = await (await OpenDmAsync(idB)).Content.ReadFromJsonAsync<DmDto>();

        Authorize(tokenC);
        (await Client.GetAsync($"/api/dm/{dm!.ChannelId}/messages"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Send_WhenBlocked_Returns403()
    {
        var (tokenA, idA) = await RegisterAsync("dm_a10", "dm_a10@test.com");
        var (tokenB, idB) = await RegisterAsync("dm_b10", "dm_b10@test.com");

        Authorize(tokenA);
        var dm = await (await OpenDmAsync(idB)).Content.ReadFromJsonAsync<DmDto>();

        // B blocks A; A can no longer send into the existing DM.
        Authorize(tokenB);
        (await Client.PostAsync($"/api/users/{idA}/block", null)).EnsureSuccessStatusCode();

        Authorize(tokenA);
        (await SendDmAsync(dm!.ChannelId, "still there?"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    private Task<HttpResponseMessage> SendDmAsync(long channelId, string content) =>
        Client.PostAsJsonAsync($"/api/dm/{channelId}/messages", new { content });

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record DmDto(
        long ChannelId,
        bool IsGroup,
        string? Name,
        long LastReadId,
        List<DmParticipantDto> Participants
    );

    private record DmParticipantDto(long UserId, string Username, string? AvatarKey);

    private record SendDto(long MessageId, long ChannelId, long? GuildId, string Content);

    private record MessagesDto(List<MessageDto> Messages, bool Degraded);

    private record MessageDto(long MessageId, string Content);
}
