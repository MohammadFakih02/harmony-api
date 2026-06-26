using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// Guild member moderation end-to-end against real Postgres: kick (removes + decrements count),
/// ban (removes + blocks rejoin until unban), timeout (sets/clears CommunicationDisabledUntil),
/// the self/non-member hierarchy guards, and permission gating (a plain member lacks KickMembers).
/// Also covers the invite-revoke ownership fix (CreateInvite-only member revokes their own invite).
/// </summary>
public class GuildMemberTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public GuildMemberTests(HarmonyWebApplicationFactory factory)
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
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Mod Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        return guild!.Id;
    }

    private async Task JoinAsync(string token, string code)
    {
        Auth(token);
        (await Client.PostAsJsonAsync($"/api/invites/{code}/join", new { })).EnsureSuccessStatusCode();
    }

    /// <summary>Owner + guild + invite + one joined member. Returns everything the tests need.</summary>
    private async Task<(string ownerToken, long ownerId, long guildId, string memberToken, long memberId, string code)>
        SetupGuildWithMemberAsync(string slug)
    {
        var (ownerToken, ownerId) = await RegisterAsync($"mod_owner_{slug}", $"mod_owner_{slug}@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var code = await CreateInviteCodeAsync(guildId); // owner is authed

        var (memberToken, memberId) = await RegisterAsync($"mod_member_{slug}", $"mod_member_{slug}@test.com");
        await JoinAsync(memberToken, code);

        return (ownerToken, ownerId, guildId, memberToken, memberId, code);
    }

    private long? TimeoutOf(long guildId, long userId)
    {
        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var member = guilds.GetMemberAsync(guildId, userId).GetAwaiter().GetResult();
        return member?.CommunicationDisabledUntil;
    }

    [Fact]
    public async Task Owner_KicksMember_RemovesThem_AndDecrementsCount()
    {
        var s = await SetupGuildWithMemberAsync("kick");

        Auth(s.ownerToken);
        var kick = await Client.DeleteAsync($"/api/guilds/{s.guildId}/members/{s.memberId}");
        kick.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var members = await Client.GetFromJsonAsync<List<GuildMemberDto>>(
            $"/api/guilds/{s.guildId}/members"
        );
        members!.Should().ContainSingle().Which.UserId.Should().Be(s.ownerId);

        var guild = await Client.GetFromJsonAsync<GuildResponse>($"/api/guilds/{s.guildId}");
        guild!.MemberCount.Should().Be(1);
    }

    [Fact]
    public async Task Owner_BansMember_BlocksRejoin_UntilUnban()
    {
        var s = await SetupGuildWithMemberAsync("ban");

        // Ban
        Auth(s.ownerToken);
        var ban = await Client.PutAsJsonAsync(
            $"/api/guilds/{s.guildId}/members/bans/{s.memberId}",
            new { reason = "spam" }
        );
        ban.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Appears in the ban list with the reason + banner identity
        var bans = await Client.GetFromJsonAsync<List<GuildBanDto>>(
            $"/api/guilds/{s.guildId}/members/bans"
        );
        bans!.Should().ContainSingle(b => b.UserId == s.memberId);
        bans!.Single().Reason.Should().Be("spam");
        bans!.Single().BannedBy.Should().Be(s.ownerId);

        // Rejoin is blocked while banned
        Auth(s.memberToken);
        var blocked = await Client.PostAsJsonAsync($"/api/invites/{s.code}/join", new { });
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Unban → rejoin succeeds
        Auth(s.ownerToken);
        var unban = await Client.DeleteAsync($"/api/guilds/{s.guildId}/members/bans/{s.memberId}");
        unban.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Auth(s.memberToken);
        var rejoin = await Client.PostAsJsonAsync($"/api/invites/{s.code}/join", new { });
        rejoin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Owner_TimesOutMember_SetsExpiry_AndClearReverts()
    {
        var s = await SetupGuildWithMemberAsync("timeout");

        Auth(s.ownerToken);
        var timeout = await Client.PutAsJsonAsync(
            $"/api/guilds/{s.guildId}/members/{s.memberId}/timeout",
            new { durationSeconds = 600 }
        );
        timeout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        TimeoutOf(s.guildId, s.memberId).Should().NotBeNull();

        var clear = await Client.DeleteAsync(
            $"/api/guilds/{s.guildId}/members/{s.memberId}/timeout"
        );
        clear.StatusCode.Should().Be(HttpStatusCode.NoContent);
        TimeoutOf(s.guildId, s.memberId).Should().BeNull();
    }

    [Fact]
    public async Task Timeout_ExceedingCap_Returns400()
    {
        var s = await SetupGuildWithMemberAsync("tocap");

        Auth(s.ownerToken);
        // 29 days > the 28-day cap.
        var timeout = await Client.PutAsJsonAsync(
            $"/api/guilds/{s.guildId}/members/{s.memberId}/timeout",
            new { durationSeconds = 29L * 24 * 60 * 60 }
        );
        timeout.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PlainMember_CannotKick_403()
    {
        var s = await SetupGuildWithMemberAsync("gate");

        // A second member for the plain member to (try to) kick.
        var (otherToken, otherId) = await RegisterAsync("mod_other_gate", "mod_other_gate@test.com");
        await JoinAsync(otherToken, s.code);

        Auth(s.memberToken); // plain member — no KickMembers
        var kick = await Client.DeleteAsync($"/api/guilds/{s.guildId}/members/{otherId}");
        kick.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_CannotKickSelf_400()
    {
        var s = await SetupGuildWithMemberAsync("self");

        Auth(s.ownerToken);
        var kick = await Client.DeleteAsync($"/api/guilds/{s.guildId}/members/{s.ownerId}");
        kick.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Kick_NonMember_Returns404()
    {
        var s = await SetupGuildWithMemberAsync("nomem");

        Auth(s.ownerToken);
        var kick = await Client.DeleteAsync($"/api/guilds/{s.guildId}/members/99999999");
        kick.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Member_CanRevokeOwnInvite_ButNotAnothersInvite()
    {
        var s = await SetupGuildWithMemberAsync("revoke");

        // Plain member mints their own invite (CreateInvite is in @everyone).
        Auth(s.memberToken);
        var mine = await Client.PostAsJsonAsync($"/api/guilds/{s.guildId}/invites", new { });
        mine.EnsureSuccessStatusCode();
        var myCode = (await mine.Content.ReadFromJsonAsync<InviteCodeDto>())!.Code;

        // Revoking their own invite works without ManageInvites.
        var revokeOwn = await Client.DeleteAsync($"/api/guilds/{s.guildId}/invites/{myCode}");
        revokeOwn.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Revoking the owner's invite (s.code) is forbidden — no ManageInvites, not the creator.
        var revokeOthers = await Client.DeleteAsync($"/api/guilds/{s.guildId}/invites/{s.code}");
        revokeOthers.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GuildCapabilities_OwnerSeesAll_PlainMemberSeesNoModBits()
    {
        var s = await SetupGuildWithMemberAsync("caps");

        Auth(s.ownerToken);
        var ownerCaps = await Client.GetFromJsonAsync<GuildCapabilitiesDto>(
            $"/api/guilds/{s.guildId}/permissions"
        );
        ownerCaps!.CanKick.Should().BeTrue();
        ownerCaps.CanBan.Should().BeTrue();
        ownerCaps.CanTimeout.Should().BeTrue();
        ownerCaps.CanManageInvites.Should().BeTrue();

        Auth(s.memberToken);
        var memberCaps = await Client.GetFromJsonAsync<GuildCapabilitiesDto>(
            $"/api/guilds/{s.guildId}/permissions"
        );
        memberCaps!.CanKick.Should().BeFalse();
        memberCaps.CanBan.Should().BeFalse();
        memberCaps.CanTimeout.Should().BeFalse();
        memberCaps.CanManageInvites.Should().BeFalse();
        // CreateInvite is in the @everyone default set.
        memberCaps.CanCreateInvite.Should().BeTrue();
    }

    // ---- response shapes (local to this fixture) ----
    private record AuthResponse(string AccessToken, UserDto User);

    private record GuildCapabilitiesDto(
        bool CanManageGuild,
        bool CanManageChannels,
        bool CanManageRoles,
        bool CanCreateInvite,
        bool CanManageInvites,
        bool CanKick,
        bool CanBan,
        bool CanTimeout,
        bool CanViewAuditLog
    );

    private record UserDto(long Id);

    private record GuildResponse(long Id, string Name, int MemberCount);

    private record GuildMemberDto(long UserId, string Username, bool IsOwner);

    private record GuildBanDto(
        long UserId,
        string? Username,
        long BannedBy,
        string? BannedByUsername,
        string? Reason,
        long CreatedAt
    );

    private record InviteCodeDto(string Code);
}
