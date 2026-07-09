using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.DirectMessages;

/// <summary>
/// REST-side tests for group DMs: create from a member set, list/visibility for every
/// participant, add a participant, leave, the minimum-size and full guards, and that a
/// pairwise block is *soft* in a group (it does not stop the send, unlike a 1:1 DM).
/// </summary>
public class GroupDmTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GroupDmTests(HarmonyWebApplicationFactory factory)
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

    private Task<HttpResponseMessage> CreateGroupAsync(string? name, params long[] userIds) =>
        Client.PostAsJsonAsync("/api/dm/group", new { name, userIds });

    private Task<List<DmDto>?> ListDmsAsync() => Client.GetFromJsonAsync<List<DmDto>>("/api/dm");

    [Fact]
    public async Task CreateGroup_ReturnsGroup_AndEveryParticipantSeesIt()
    {
        var (tokenA, _) = await RegisterAsync("gdm_a1", "gdm_a1@test.com");
        var (tokenB, idB) = await RegisterAsync("gdm_b1", "gdm_b1@test.com");
        var (tokenC, idC) = await RegisterAsync("gdm_c1", "gdm_c1@test.com");

        Authorize(tokenA);
        var create = await CreateGroupAsync("Squad", idB, idC);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var group = await create.Content.ReadFromJsonAsync<DmDto>();
        group!.IsGroup.Should().BeTrue();
        group.Name.Should().Be("Squad");
        group.Participants.Select(p => p.UserId).Should().BeEquivalentTo(new[] { idB, idC });

        // Each other member sees the group, with the *other* two participants listed (caller excluded).
        Authorize(tokenB);
        var bList = await ListDmsAsync();
        bList.Should().ContainSingle(d => d.ChannelId == group.ChannelId && d.IsGroup);

        Authorize(tokenC);
        var cList = await ListDmsAsync();
        cList.Should().ContainSingle(d => d.ChannelId == group.ChannelId);
    }

    [Fact]
    public async Task CreateGroup_WithFewerThanTwoOthers_Returns400()
    {
        var (tokenA, _) = await RegisterAsync("gdm_a2", "gdm_a2@test.com");
        var (_, idB) = await RegisterAsync("gdm_b2", "gdm_b2@test.com");

        Authorize(tokenA);
        (await CreateGroupAsync("TooSmall", idB)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddParticipant_LetsTheNewMemberSeeTheGroup()
    {
        var (tokenA, _) = await RegisterAsync("gdm_a3", "gdm_a3@test.com");
        var (_, idB) = await RegisterAsync("gdm_b3", "gdm_b3@test.com");
        var (_, idC) = await RegisterAsync("gdm_c3", "gdm_c3@test.com");
        var (tokenD, idD) = await RegisterAsync("gdm_d3", "gdm_d3@test.com");

        Authorize(tokenA);
        var group = await (await CreateGroupAsync("Growing", idB, idC)).Content.ReadFromJsonAsync<DmDto>();

        // D is not a member yet.
        Authorize(tokenD);
        (await ListDmsAsync()).Should().BeEmpty();

        // Any participant may add — A adds D.
        Authorize(tokenA);
        var add = await Client.PostAsJsonAsync(
            $"/api/dm/{group!.ChannelId}/participants",
            new { userId = idD }
        );
        add.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Authorize(tokenD);
        (await ListDmsAsync()).Should().ContainSingle(d => d.ChannelId == group.ChannelId);
    }

    [Fact]
    public async Task Leave_RemovesTheGroupFromTheCallersList()
    {
        var (tokenA, _) = await RegisterAsync("gdm_a4", "gdm_a4@test.com");
        var (tokenB, idB) = await RegisterAsync("gdm_b4", "gdm_b4@test.com");
        var (_, idC) = await RegisterAsync("gdm_c4", "gdm_c4@test.com");

        Authorize(tokenA);
        var group = await (await CreateGroupAsync("Leavers", idB, idC)).Content.ReadFromJsonAsync<DmDto>();

        Authorize(tokenB);
        var leave = await Client.DeleteAsync($"/api/dm/{group!.ChannelId}/participants/me");
        leave.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListDmsAsync()).Should().BeEmpty();

        // The remaining members still have it.
        Authorize(tokenA);
        (await ListDmsAsync()).Should().ContainSingle(d => d.ChannelId == group.ChannelId);
    }

    [Fact]
    public async Task Leave_OnAOneToOneDm_Returns400()
    {
        var (tokenA, _) = await RegisterAsync("gdm_a5", "gdm_a5@test.com");
        var (_, idB) = await RegisterAsync("gdm_b5", "gdm_b5@test.com");

        Authorize(tokenA);
        var dm = await (await Client.PostAsJsonAsync("/api/dm", new { targetUserId = idB }))
            .Content.ReadFromJsonAsync<DmDto>();

        (await Client.DeleteAsync($"/api/dm/{dm!.ChannelId}/participants/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Send_InGroup_IsSoftOnBlocks_AndDeliversToAll()
    {
        var (tokenA, idA) = await RegisterAsync("gdm_a6", "gdm_a6@test.com");
        var (tokenB, idB) = await RegisterAsync("gdm_b6", "gdm_b6@test.com");
        var (tokenC, idC) = await RegisterAsync("gdm_c6", "gdm_c6@test.com");

        Authorize(tokenA);
        var group = await (await CreateGroupAsync("Blockers", idB, idC)).Content.ReadFromJsonAsync<DmDto>();

        // B blocks A. In a 1:1 this would 403 the send; in a group it's soft.
        Authorize(tokenB);
        (await Client.PostAsync($"/api/users/{idA}/block", null)).EnsureSuccessStatusCode();

        Authorize(tokenA);
        var send = await Client.PostAsJsonAsync(
            $"/api/dm/{group!.ChannelId}/messages",
            new { content = "hey everyone" }
        );
        send.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = await send.Content.ReadFromJsonAsync<SendDto>();
        sent!.GuildId.Should().BeNull();

        // C (not blocking anyone) reads it once the consumer has persisted it.
        Authorize(tokenC);
        await Eventually.GetAsync(
            action: async () =>
            {
                var resp = await Client.GetAsync($"/api/dm/{group.ChannelId}/messages");
                if (!resp.IsSuccessStatusCode)
                    return [];
                var body = await resp.Content.ReadFromJsonAsync<MessagesDto>();
                return body?.Messages.ToList() ?? [];
            },
            predicate: msgs => msgs.Any(m => m.MessageId == sent.MessageId && m.Content == "hey everyone"),
            retries: 100,
            intervalMs: 100
        );
    }

    [Fact]
    public async Task AddParticipant_PostsAGroupJoinNotice()
    {
        var (tokenA, _) = await RegisterAsync("gdm_jn_a", "gdm_jn_a@test.com");
        var (_, idB) = await RegisterAsync("gdm_jn_b", "gdm_jn_b@test.com");
        var (_, idC) = await RegisterAsync("gdm_jn_c", "gdm_jn_c@test.com");
        var (_, idD) = await RegisterAsync("gdm_jn_d", "gdm_jn_d@test.com");

        Authorize(tokenA);
        var group = await (await CreateGroupAsync("Joiners", idB, idC)).Content.ReadFromJsonAsync<DmDto>();

        var add = await Client.PostAsJsonAsync(
            $"/api/dm/{group!.ChannelId}/participants",
            new { userId = idD }
        );
        add.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The notice rides the normal async pipeline — poll history until the consumer lands it.
        // Author = the added user; the client renders "X joined the group" from the type.
        await Eventually.GetAsync(
            action: () => GetMessagesAsync(group.ChannelId),
            predicate: msgs => msgs.Any(m => m.MessageType == "group_join" && m.UserId == idD),
            retries: 100,
            intervalMs: 100
        );
    }

    [Fact]
    public async Task Leave_PostsAGroupLeaveNotice()
    {
        var (tokenA, _) = await RegisterAsync("gdm_lv_a", "gdm_lv_a@test.com");
        var (tokenB, idB) = await RegisterAsync("gdm_lv_b", "gdm_lv_b@test.com");
        var (_, idC) = await RegisterAsync("gdm_lv_c", "gdm_lv_c@test.com");

        Authorize(tokenA);
        var group = await (await CreateGroupAsync("Quitters", idB, idC)).Content.ReadFromJsonAsync<DmDto>();

        Authorize(tokenB);
        (await Client.DeleteAsync($"/api/dm/{group!.ChannelId}/participants/me"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        // A remaining member sees the leave notice authored by the leaver.
        Authorize(tokenA);
        await Eventually.GetAsync(
            action: () => GetMessagesAsync(group.ChannelId),
            predicate: msgs => msgs.Any(m => m.MessageType == "group_leave" && m.UserId == idB),
            retries: 100,
            intervalMs: 100
        );
    }

    [Fact]
    public async Task AddParticipant_ByNonMember_Returns403()
    {
        var (tokenA, _) = await RegisterAsync("gdm_a7", "gdm_a7@test.com");
        var (_, idB) = await RegisterAsync("gdm_b7", "gdm_b7@test.com");
        var (_, idC) = await RegisterAsync("gdm_c7", "gdm_c7@test.com");
        var (tokenD, idD) = await RegisterAsync("gdm_d7", "gdm_d7@test.com");

        Authorize(tokenA);
        var group = await (await CreateGroupAsync("Closed", idB, idC)).Content.ReadFromJsonAsync<DmDto>();

        // D is not a member and cannot add anyone.
        Authorize(tokenD);
        (await Client.PostAsJsonAsync($"/api/dm/{group!.ChannelId}/participants", new { userId = idD }))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RenameGroup_ByAParticipant_IsReflectedForEveryone()
    {
        var (tokenA, _) = await RegisterAsync("gdm_rn_a", "gdm_rn_a@test.com");
        var (tokenB, idB) = await RegisterAsync("gdm_rn_b", "gdm_rn_b@test.com");
        var (_, idC) = await RegisterAsync("gdm_rn_c", "gdm_rn_c@test.com");

        Authorize(tokenA);
        var create = await CreateGroupAsync("Before", idB, idC);
        var group = await create.Content.ReadFromJsonAsync<DmDto>();

        // Any participant (not just the creator) can rename.
        Authorize(tokenB);
        var rename = await Client.PatchAsJsonAsync(
            $"/api/dm/{group!.ChannelId}/name",
            new { name = "After" }
        );
        rename.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Authorize(tokenA);
        var list = await ListDmsAsync();
        list.Should().ContainSingle(d => d.ChannelId == group.ChannelId && d.Name == "After");
    }

    [Fact]
    public async Task RenameGroup_ByANonParticipant_IsForbidden()
    {
        var (tokenA, _) = await RegisterAsync("gdm_rf_a", "gdm_rf_a@test.com");
        var (_, idB) = await RegisterAsync("gdm_rf_b", "gdm_rf_b@test.com");
        var (_, idC) = await RegisterAsync("gdm_rf_c", "gdm_rf_c@test.com");
        var (tokenD, _) = await RegisterAsync("gdm_rf_d", "gdm_rf_d@test.com");

        Authorize(tokenA);
        var create = await CreateGroupAsync("Locked", idB, idC);
        var group = await create.Content.ReadFromJsonAsync<DmDto>();

        Authorize(tokenD);
        var rename = await Client.PatchAsJsonAsync(
            $"/api/dm/{group!.ChannelId}/name",
            new { name = "Hijacked" }
        );
        rename.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RenameOneToOneDm_IsRejected()
    {
        var (tokenA, _) = await RegisterAsync("gdm_r1_a", "gdm_r1_a@test.com");
        var (_, idB) = await RegisterAsync("gdm_r1_b", "gdm_r1_b@test.com");

        Authorize(tokenA);
        var open = await Client.PostAsJsonAsync("/api/dm", new { targetUserId = idB });
        open.EnsureSuccessStatusCode();
        var dm = await open.Content.ReadFromJsonAsync<DmDto>();

        var rename = await Client.PatchAsJsonAsync($"/api/dm/{dm!.ChannelId}/name", new { name = "Nope" });
        rename.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

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

    private record MessageDto(long MessageId, long UserId, string Content, string MessageType);

    private async Task<List<MessageDto>> GetMessagesAsync(long channelId)
    {
        var resp = await Client.GetAsync($"/api/dm/{channelId}/messages");
        if (!resp.IsSuccessStatusCode)
            return [];
        var body = await resp.Content.ReadFromJsonAsync<MessagesDto>();
        return body?.Messages.ToList() ?? [];
    }
}
