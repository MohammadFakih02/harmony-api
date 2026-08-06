using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Permissions;

/// <summary>
/// CRUD for channel permission overrides against the real Postgres + Redis stack:
/// edits are ManageRoles-gated, persist correctly, flow through the permission resolver
/// when a channelId is supplied, and invalidate the cache so the next resolve sees the change.
/// </summary>
public class ChannelOverrideTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public ChannelOverrideTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private static bool Has(long bits, Permission p) => (bits & (long)p) == (long)p;

    private async Task<string> RegisterAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.AccessToken;
    }

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<(long guildId, string inviteCode)> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Override Guild" });
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
        var channel = await resp.Content.ReadFromJsonAsync<ChannelResponse>();
        return channel!.Id;
    }

    private async Task<long> JoinAndGetMemberIdAsync(string memberToken, string invite, long guildId, long ownerId)
    {
        Auth(memberToken);
        var joinResp = await Client.PostAsJsonAsync($"/api/invites/{invite}/join", new { });
        joinResp.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        return (await guilds.GetMemberIdsAsync(guildId)).Single(id => id != ownerId);
    }

    private static (long ownerId, long everyoneRoleId) GuildFacts(HarmonyWebApplicationFactory factory, long guildId)
    {
        using var scope = factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var ownerId = guilds.GetByIdAsync(guildId).GetAwaiter().GetResult()!.OwnerId;
        var everyone = roles.GetDefaultRoleAsync(guildId).GetAwaiter().GetResult()!;
        return (ownerId, everyone.Id);
    }

    private async Task<long> ResolveAsync(long userId, long guildId, long? channelId)
    {
        using var scope = Factory.Services.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        return await permissions.ResolveAsync(userId, guildId, channelId);
    }

    [Fact]
    public async Task Owner_CanUpsertRoleOverride_AndItIsListed()
    {
        var token = await RegisterAsync("ovowner1", "ovowner1@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var (_, everyoneId) = GuildFacts(Factory, guildId);

        Auth(token);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.SendMessage }
        );
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await Client.GetFromJsonAsync<List<ChannelOverrideResponse>>(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides"
        );
        list.Should().ContainSingle(o => o.TargetId == everyoneId && o.DenyBits == (long)Permission.SendMessage);
    }

    [Fact]
    public async Task EveryoneDeny_DropsBitFromMemberChannelResolve_ButNotGuildLevel()
    {
        var ownerToken = await RegisterAsync("ovowner2", "ovowner2@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var (ownerId, everyoneId) = GuildFacts(Factory, guildId);

        var memberId = await JoinAndGetMemberIdAsync(
            await RegisterAsync("ovmember2", "ovmember2@test.com"), invite, guildId, ownerId
        );

        // Warm the cache at both scopes; the member starts with SendMessage everywhere.
        Has(await ResolveAsync(memberId, guildId, null), Permission.SendMessage).Should().BeTrue();
        Has(await ResolveAsync(memberId, guildId, channelId), Permission.SendMessage).Should().BeTrue();

        Auth(ownerToken);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.SendMessage }
        );
        put.EnsureSuccessStatusCode();

        // Channel-scoped resolve loses it; guild-level is unaffected — and the cache was invalidated.
        Has(await ResolveAsync(memberId, guildId, channelId), Permission.SendMessage).Should().BeFalse();
        Has(await ResolveAsync(memberId, guildId, null), Permission.SendMessage).Should().BeTrue();
    }

    [Fact]
    public async Task MemberAllowOverride_GrantsOtherwiseAbsentBit()
    {
        var ownerToken = await RegisterAsync("ovowner3", "ovowner3@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var (ownerId, _) = GuildFacts(Factory, guildId);

        var memberId = await JoinAndGetMemberIdAsync(
            await RegisterAsync("ovmember3", "ovmember3@test.com"), invite, guildId, ownerId
        );

        // A plain member lacks BanMembers anywhere.
        Has(await ResolveAsync(memberId, guildId, channelId), Permission.BanMembers).Should().BeFalse();

        Auth(ownerToken);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{memberId}",
            new { targetType = "user", allowBits = (long)Permission.BanMembers, denyBits = 0L }
        );
        put.EnsureSuccessStatusCode();

        Has(await ResolveAsync(memberId, guildId, channelId), Permission.BanMembers).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_RevertsResolution()
    {
        var ownerToken = await RegisterAsync("ovowner4", "ovowner4@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var (ownerId, everyoneId) = GuildFacts(Factory, guildId);

        var memberId = await JoinAndGetMemberIdAsync(
            await RegisterAsync("ovmember4", "ovmember4@test.com"), invite, guildId, ownerId
        );

        Auth(ownerToken);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.SendMessage }
        );
        put.EnsureSuccessStatusCode();
        Has(await ResolveAsync(memberId, guildId, channelId), Permission.SendMessage).Should().BeFalse();

        var del = await Client.DeleteAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}"
        );
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Has(await ResolveAsync(memberId, guildId, channelId), Permission.SendMessage).Should().BeTrue();
    }

    [Fact]
    public async Task Viewers_ExcludeMembersDeniedViewChannel_ButKeepOwner()
    {
        var ownerToken = await RegisterAsync("ovowner8", "ovowner8@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var (ownerId, everyoneId) = GuildFacts(Factory, guildId);

        var memberId = await JoinAndGetMemberIdAsync(
            await RegisterAsync("ovmember8", "ovmember8@test.com"), invite, guildId, ownerId
        );

        // Both can view the channel to start with.
        Auth(ownerToken);
        var before = await Client.GetFromJsonAsync<List<long>>(
            $"/api/guilds/{guildId}/channels/{channelId}/viewers"
        );
        before.Should().Contain(new[] { ownerId, memberId });

        // Deny ViewChannel to @everyone (the #staff pattern). The owner bypasses overrides.
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.ViewChannel }
        );
        put.EnsureSuccessStatusCode();

        var after = await Client.GetFromJsonAsync<List<long>>(
            $"/api/guilds/{guildId}/channels/{channelId}/viewers"
        );
        after.Should().Contain(ownerId).And.NotContain(memberId);
    }

    [Fact]
    public async Task NonManageRolesMember_IsForbidden()
    {
        var ownerToken = await RegisterAsync("ovowner5", "ovowner5@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);
        var (ownerId, everyoneId) = GuildFacts(Factory, guildId);

        var memberToken = await RegisterAsync("ovmember5", "ovmember5@test.com");
        await JoinAndGetMemberIdAsync(memberToken, invite, guildId, ownerId);

        Auth(memberToken);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.SendMessage }
        );
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Upsert_IsIdempotent_UpdatesNotDuplicates()
    {
        var token = await RegisterAsync("ovowner6", "ovowner6@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var (_, everyoneId) = GuildFacts(Factory, guildId);

        Auth(token);
        var url = $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}";

        (await Client.PutAsJsonAsync(url,
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.SendMessage }))
            .EnsureSuccessStatusCode();
        (await Client.PutAsJsonAsync(url,
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.AddReactions }))
            .EnsureSuccessStatusCode();

        var list = await Client.GetFromJsonAsync<List<ChannelOverrideResponse>>(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides"
        );
        list.Should().ContainSingle();
        list![0].DenyBits.Should().Be((long)Permission.AddReactions);
    }

    [Fact]
    public async Task Upsert_RejectsOverlappingAllowAndDeny()
    {
        var token = await RegisterAsync("ovowner7", "ovowner7@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);
        var (_, everyoneId) = GuildFacts(Factory, guildId);

        Auth(token);
        var put = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/overrides/{everyoneId}",
            new
            {
                targetType = "role",
                allowBits = (long)Permission.SendMessage,
                denyBits = (long)Permission.SendMessage,
            }
        );
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(long Id, string Name);

    private record ChannelResponse(long Id, long? GuildId, string Name, string Type);

    private record ChannelOverrideResponse(
        long Id,
        long ChannelId,
        long TargetId,
        string TargetType,
        long AllowBits,
        long DenyBits
    );
}
