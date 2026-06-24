using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Permissions;

/// <summary>
/// End-to-end enforcement of resolved permission bits on REST + the message path:
/// the permission attribute gates management endpoints, the message service gates
/// send/read on resolved bits and the timeout, and channel overrides take effect.
/// </summary>
public class PermissionEnforcementTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public PermissionEnforcementTests(HarmonyWebApplicationFactory factory)
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

    private async Task<(long guildId, string invite)> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Enforce Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        return (guild!.Id, await CreateInviteCodeAsync(guild.Id));
    }

    private async Task<long> CreateChannelAsync(string ownerToken, long guildId)
    {
        Auth(ownerToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "general", type = "text", position = 0 }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private async Task<long> JoinAsync(string memberToken, string invite, long guildId, long ownerId)
    {
        Auth(memberToken);
        (await Client.PostAsJsonAsync($"/api/invites/{invite}/join", new { })).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        return (await guilds.GetMemberIdsAsync(guildId)).Single(id => id != ownerId);
    }

    private static long OwnerOf(HarmonyWebApplicationFactory factory, long guildId)
    {
        using var scope = factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        return guilds.GetByIdAsync(guildId).GetAwaiter().GetResult()!.OwnerId;
    }

    private static long EveryoneRoleOf(HarmonyWebApplicationFactory factory, long guildId)
    {
        using var scope = factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        return roles.GetDefaultRoleAsync(guildId).GetAwaiter().GetResult()!.Id;
    }

    private async Task SetTimeoutAsync(long guildId, long userId, long untilUnixMs)
    {
        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var member = await guilds.GetMemberAsync(guildId, userId);
        member!.CommunicationDisabledUntil = untilUnixMs;
        await guilds.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> SendAsync(long guildId, long channelId) =>
        Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content = "hello" }
        );

    private Task<HttpResponseMessage> DenyEveryoneAsync(
        long guildId, long channelId, long everyoneId, Permission deny
    ) =>
        Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)deny }
        );

    [Fact]
    public async Task Member_CanSendMessage_Normally()
    {
        var ownerToken = await RegisterAsync("enfowner1", "enfowner1@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        var memberToken = await RegisterAsync("enfmember1", "enfmember1@test.com");
        await JoinAsync(memberToken, invite, guildId, OwnerOf(Factory, guildId));

        Auth(memberToken);
        (await SendAsync(guildId, channelId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TimedOutMember_CannotSendMessage()
    {
        var ownerToken = await RegisterAsync("enfowner2", "enfowner2@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        var memberToken = await RegisterAsync("enfmember2", "enfmember2@test.com");
        var memberId = await JoinAsync(memberToken, invite, guildId, OwnerOf(Factory, guildId));

        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        await SetTimeoutAsync(guildId, memberId, future);

        Auth(memberToken);
        (await SendAsync(guildId, channelId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MemberDeniedSendViaOverride_Returns403()
    {
        var ownerToken = await RegisterAsync("enfowner3", "enfowner3@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var everyoneId = EveryoneRoleOf(Factory, guildId);

        var memberToken = await RegisterAsync("enfmember3", "enfmember3@test.com");
        await JoinAsync(memberToken, invite, guildId, OwnerOf(Factory, guildId));

        Auth(ownerToken);
        (await DenyEveryoneAsync(guildId, channelId, everyoneId, Permission.SendMessage))
            .EnsureSuccessStatusCode();

        Auth(memberToken);
        (await SendAsync(guildId, channelId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MemberDeniedViewViaOverride_CannotReadMessages()
    {
        var ownerToken = await RegisterAsync("enfowner4", "enfowner4@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var everyoneId = EveryoneRoleOf(Factory, guildId);

        var memberToken = await RegisterAsync("enfmember4", "enfmember4@test.com");
        await JoinAsync(memberToken, invite, guildId, OwnerOf(Factory, guildId));

        // Sanity: the member can read before the override.
        Auth(memberToken);
        (await Client.GetAsync($"/api/guilds/{guildId}/channels/{channelId}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Auth(ownerToken);
        (await DenyEveryoneAsync(guildId, channelId, everyoneId, Permission.ViewChannel))
            .EnsureSuccessStatusCode();

        Auth(memberToken);
        (await Client.GetAsync($"/api/guilds/{guildId}/channels/{channelId}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NonManageChannelsMember_CannotCreateChannel()
    {
        var ownerToken = await RegisterAsync("enfowner5", "enfowner5@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);

        var memberToken = await RegisterAsync("enfmember5", "enfmember5@test.com");
        await JoinAsync(memberToken, invite, guildId, OwnerOf(Factory, guildId));

        Auth(memberToken);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "sneaky", type = "text", position = 0 }
        );
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NonMember_CannotReadMessages()
    {
        var ownerToken = await RegisterAsync("enfowner6", "enfowner6@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        var outsiderToken = await RegisterAsync("enfoutsider6", "enfoutsider6@test.com");
        Auth(outsiderToken);
        (await Client.GetAsync($"/api/guilds/{guildId}/channels/{channelId}/messages"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChannelList_OmitsChannelHiddenByOverride()
    {
        var ownerToken = await RegisterAsync("enfowner7", "enfowner7@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var everyoneId = EveryoneRoleOf(Factory, guildId);

        var memberToken = await RegisterAsync("enfmember7", "enfmember7@test.com");
        await JoinAsync(memberToken, invite, guildId, OwnerOf(Factory, guildId));

        Auth(ownerToken);
        (await DenyEveryoneAsync(guildId, channelId, everyoneId, Permission.ViewChannel))
            .EnsureSuccessStatusCode();

        // Member: the override-hidden channel is absent from the list entirely.
        Auth(memberToken);
        var memberList = await Client.GetFromJsonAsync<List<ChannelDto>>(
            $"/api/guilds/{guildId}/channels");
        memberList.Should().NotContain(c => c.Id == channelId);

        // Owner: still sees it (owner bypasses overrides).
        Auth(ownerToken);
        var ownerList = await Client.GetFromJsonAsync<List<ChannelDto>>(
            $"/api/guilds/{guildId}/channels");
        ownerList.Should().Contain(c => c.Id == channelId);
    }

    [Fact]
    public async Task Capabilities_ReflectTimeoutAndPermissions()
    {
        var ownerToken = await RegisterAsync("enfowner8", "enfowner8@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        var memberToken = await RegisterAsync("enfmember8", "enfmember8@test.com");
        var memberId = await JoinAsync(memberToken, invite, guildId, OwnerOf(Factory, guildId));

        var capsUrl = $"/api/guilds/{guildId}/channels/{channelId}/permissions";

        // Plain member: can view + send, not timed out, no management.
        Auth(memberToken);
        var caps = await Client.GetFromJsonAsync<CapsDto>(capsUrl);
        caps!.CanView.Should().BeTrue();
        caps.CanSend.Should().BeTrue();
        caps.TimedOut.Should().BeFalse();
        caps.CanManageMessages.Should().BeFalse();

        // After a timeout: canView stays, canSend flips off, timedOut true.
        await SetTimeoutAsync(guildId, memberId, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        Auth(memberToken);
        var muted = await Client.GetFromJsonAsync<CapsDto>(capsUrl);
        muted!.CanView.Should().BeTrue();
        muted.CanSend.Should().BeFalse();
        muted.TimedOut.Should().BeTrue();
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(long Id, string Name);

    private record ChannelResponse(long Id, long? GuildId, string Name, string Type);

    private record ChannelDto(long Id, string Name, string Type);

    private record CapsDto(bool CanView, bool CanSend, bool CanManageMessages, bool CanManageChannels, bool TimedOut);
}
