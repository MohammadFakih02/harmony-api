using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Enums;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// Role management end-to-end against real Postgres + Redis: create/list/delete, the @everyone guard,
/// permission gating, and that assigning a role with a permission bit actually grants the capability
/// (proves cache invalidation + resolver wiring through the live stack).
/// </summary>
public class RoleTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public RoleTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

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

    private void Auth(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<long> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Role Guild" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<GuildResponse>())!.Id;
    }

    private async Task<(string ownerToken, long guildId, string memberToken, long memberId, string code)>
        SetupAsync(string slug)
    {
        var (ownerToken, _) = await RegisterAsync($"role_owner_{slug}", $"role_owner_{slug}@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var code = await CreateInviteCodeAsync(guildId);
        var (memberToken, memberId) = await RegisterAsync($"role_member_{slug}", $"role_member_{slug}@test.com");
        Auth(memberToken);
        (await Client.PostAsJsonAsync($"/api/invites/{code}/join", new { })).EnsureSuccessStatusCode();
        return (ownerToken, guildId, memberToken, memberId, code);
    }

    private async Task<RoleDto> CreateRoleAsync(string token, long guildId, object body)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync($"/api/guilds/{guildId}/roles", body);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RoleDto>())!;
    }

    [Fact]
    public async Task Owner_CreatesRole_AppearsInList()
    {
        var s = await SetupAsync("create");

        var role = await CreateRoleAsync(s.ownerToken, s.guildId,
            new { name = "Moderator", color = 0xE74C3C, permissionBits = (long)Permission.ManageMessages });
        role.Name.Should().Be("Moderator");
        role.IsDefault.Should().BeFalse();

        Auth(s.ownerToken);
        var list = await Client.GetFromJsonAsync<List<RoleDto>>($"/api/guilds/{s.guildId}/roles");
        list.Should().Contain(r => r.Id == role.Id && r.Name == "Moderator");
        // @everyone is always present.
        list.Should().Contain(r => r.IsDefault);
    }

    [Fact]
    public async Task AssigningRoleWithKick_GrantsCapability_AndUnassignRevokes()
    {
        var s = await SetupAsync("assign");

        // Sanity: the plain member can't kick yet.
        Auth(s.memberToken);
        var before = await Client.GetFromJsonAsync<GuildCapabilitiesDto>($"/api/guilds/{s.guildId}/permissions");
        before!.CanKick.Should().BeFalse();

        var role = await CreateRoleAsync(s.ownerToken, s.guildId,
            new { name = "Kickers", permissionBits = (long)Permission.KickMembers });

        Auth(s.ownerToken);
        (await Client.PutAsync($"/api/guilds/{s.guildId}/roles/{role.Id}/members/{s.memberId}", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The assignment invalidated the member's cached perms → they now resolve KickMembers.
        Auth(s.memberToken);
        var after = await Client.GetFromJsonAsync<GuildCapabilitiesDto>($"/api/guilds/{s.guildId}/permissions");
        after!.CanKick.Should().BeTrue();

        // Unassign → capability revoked again.
        Auth(s.ownerToken);
        (await Client.DeleteAsync($"/api/guilds/{s.guildId}/roles/{role.Id}/members/{s.memberId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        Auth(s.memberToken);
        var revoked = await Client.GetFromJsonAsync<GuildCapabilitiesDto>($"/api/guilds/{s.guildId}/permissions");
        revoked!.CanKick.Should().BeFalse();
    }

    [Fact]
    public async Task PlainMember_CannotCreateRole_403()
    {
        var s = await SetupAsync("gate");

        Auth(s.memberToken);
        var resp = await Client.PostAsJsonAsync($"/api/guilds/{s.guildId}/roles", new { name = "Nope" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteEveryoneRole_Returns400()
    {
        var s = await SetupAsync("everyone");

        Auth(s.ownerToken);
        var list = await Client.GetFromJsonAsync<List<RoleDto>>($"/api/guilds/{s.guildId}/roles");
        var everyone = list!.Single(r => r.IsDefault);

        var resp = await Client.DeleteAsync($"/api/guilds/{s.guildId}/roles/{everyone.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Owner_DeletesRole_RemovesFromList()
    {
        var s = await SetupAsync("delete");
        var role = await CreateRoleAsync(s.ownerToken, s.guildId, new { name = "Temp" });

        Auth(s.ownerToken);
        (await Client.DeleteAsync($"/api/guilds/{s.guildId}/roles/{role.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Client.GetFromJsonAsync<List<RoleDto>>($"/api/guilds/{s.guildId}/roles");
        list.Should().NotContain(r => r.Id == role.Id);
    }

    // ---- response shapes ----
    private record AuthResponse(string AccessToken, UserDto User);
    private record UserDto(long Id);
    private record GuildResponse(long Id, string Name);
    private record RoleDto(long Id, string Name, int Color, long PermissionBits, int Position, bool IsDefault);
    private record GuildCapabilitiesDto(bool CanKick, bool CanBan, bool CanTimeout);
}
