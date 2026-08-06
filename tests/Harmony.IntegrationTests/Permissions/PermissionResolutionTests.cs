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
/// End-to-end permission resolution against the real Postgres + Redis stack:
/// guild creation seeds @everyone, the owner resolves to all permissions, a plain
/// member resolves to the default member set, and cache invalidation behaves.
/// </summary>
public class PermissionResolutionTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public PermissionResolutionTests(HarmonyWebApplicationFactory factory)
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

    private async Task<(long guildId, string inviteCode)> CreateGuildAsync(string token)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Perm Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        return (guild!.Id, await CreateInviteCodeAsync(guild.Id));
    }

    [Fact]
    public async Task GuildCreation_SeedsEveryoneRole_WithDefaultMemberPermissions()
    {
        var token = await RegisterAsync("permowner1", "permowner1@test.com");
        var (guildId, _) = await CreateGuildAsync(token);

        using var scope = Factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        var everyone = await roles.GetDefaultRoleAsync(guildId);

        everyone.Should().NotBeNull();
        everyone!.Name.Should().Be("@everyone");
        everyone.IsDefault.Should().BeTrue();
        everyone.PermissionBits.Should().Be((long)Permission.DefaultEveryone);
    }

    [Fact]
    public async Task Owner_ResolvesToAllPermissions()
    {
        var token = await RegisterAsync("permowner2", "permowner2@test.com");
        var (guildId, _) = await CreateGuildAsync(token);

        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        var ownerId = (await guilds.GetByIdAsync(guildId))!.OwnerId;
        var bits = await permissions.ResolveAsync(ownerId, guildId);

        Has(bits, Permission.Administrator).Should().BeTrue();
        Has(bits, Permission.ManageGuild).Should().BeTrue();
        Has(bits, Permission.BanMembers).Should().BeTrue();
    }

    [Fact]
    public async Task PlainMember_ResolvesToDefaultMemberSet()
    {
        var ownerToken = await RegisterAsync("permowner3", "permowner3@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);

        var memberToken = await RegisterAsync("permmember3", "permmember3@test.com");
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", memberToken);
        var joinResp = await Client.PostAsJsonAsync($"/api/invites/{invite}/join", new { });
        joinResp.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        var ownerId = (await guilds.GetByIdAsync(guildId))!.OwnerId;
        var memberId = (await guilds.GetMemberIdsAsync(guildId)).Single(id => id != ownerId);

        var bits = await permissions.ResolveAsync(memberId, guildId);

        Has(bits, Permission.SendMessage).Should().BeTrue();
        Has(bits, Permission.ViewChannel).Should().BeTrue();
        Has(bits, Permission.BanMembers).Should().BeFalse();
        Has(bits, Permission.Administrator).Should().BeFalse();
    }

    [Fact]
    public async Task NonMember_ResolvesToZero()
    {
        var ownerToken = await RegisterAsync("permowner4", "permowner4@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);

        // An outsider who never joined.
        await RegisterAsync("permoutsider4", "permoutsider4@test.com");

        using var scope = Factory.Services.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        // A user id guaranteed not to be a member of this guild.
        var bits = await permissions.ResolveAsync(userId: 999_999_999, guildId, channelId: null);

        bits.Should().Be(0);
    }

    [Fact]
    public async Task InvalidateUser_ForcesRecompute_WithoutError()
    {
        var token = await RegisterAsync("permowner5", "permowner5@test.com");
        var (guildId, _) = await CreateGuildAsync(token);

        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        var ownerId = (await guilds.GetByIdAsync(guildId))!.OwnerId;

        var first = await permissions.ResolveAsync(ownerId, guildId); // populates cache
        await permissions.InvalidateUserAsync(ownerId, guildId);       // drops it
        var second = await permissions.ResolveAsync(ownerId, guildId); // recomputes

        second.Should().Be(first);
    }

    private record AuthResponse(string AccessToken);

    private record GuildResponse(long Id, string Name);
}
