using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// Managed guild invites end-to-end against real Postgres: create (CreateInvite — every member
/// has it by default), list/delete (ManageInvites-gated), preview, and redeem (use-count bump,
/// max-uses + expiry enforcement, already-member + invalid-code handling).
/// </summary>
public class InviteTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public InviteTests(HarmonyWebApplicationFactory factory)
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
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Invite Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<GuildResponse>();
        return guild!.Id;
    }

    private async Task<InviteResponse> CreateInviteAsync(string token, long guildId, object? body = null)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync($"/api/guilds/{guildId}/invites", body ?? new { });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<InviteResponse>())!;
    }

    private long OwnerIdOf(long guildId)
    {
        using var scope = Factory.Services.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        return guilds.GetByIdAsync(guildId).GetAwaiter().GetResult()!.OwnerId;
    }

    [Fact]
    public async Task Owner_CreatesInvite_AppearsInListWithCreatorIdentity()
    {
        var (token, _) = await RegisterAsync("inv_owner1", "inv_owner1@test.com");
        var guildId = await CreateGuildAsync(token);

        var created = await CreateInviteAsync(token, guildId);
        created.Code.Should().NotBeNullOrWhiteSpace();
        created.UseCount.Should().Be(0);
        created.ChannelId.Should().BeNull(); // guild-level by default

        Auth(token);
        var list = await Client.GetFromJsonAsync<List<InviteResponse>>(
            $"/api/guilds/{guildId}/invites"
        );
        list.Should()
            .ContainSingle(i => i.Code == created.Code)
            .Which.CreatorUsername.Should()
            .Be("inv_owner1");
    }

    [Fact]
    public async Task PlainMember_HasCreateInvite_ButNotManageInvites()
    {
        var (ownerToken, _) = await RegisterAsync("inv_owner2", "inv_owner2@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var seed = await CreateInviteAsync(ownerToken, guildId);

        var (memberToken, _) = await RegisterAsync("inv_member2", "inv_member2@test.com");
        Auth(memberToken);
        (await Client.PostAsJsonAsync($"/api/invites/{seed.Code}/join", new { }))
            .EnsureSuccessStatusCode();

        // CreateInvite is part of the @everyone default set → a plain member can mint one.
        Auth(memberToken);
        var create = await Client.PostAsJsonAsync($"/api/guilds/{guildId}/invites", new { });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        // ManageInvites is not in the default set → listing is forbidden.
        var list = await Client.GetAsync($"/api/guilds/{guildId}/invites");
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Preview_ReturnsGuildSummary_AndJoinIncrementsUseCount()
    {
        var (ownerToken, _) = await RegisterAsync("inv_owner3", "inv_owner3@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var invite = await CreateInviteAsync(ownerToken, guildId);

        var (memberToken, _) = await RegisterAsync("inv_member3", "inv_member3@test.com");
        Auth(memberToken);

        var preview = await Client.GetFromJsonAsync<InvitePreviewResponse>(
            $"/api/invites/{invite.Code}"
        );
        preview!.GuildId.Should().Be(guildId);
        preview.GuildName.Should().Be("Invite Guild");
        preview.MemberCount.Should().Be(1);

        var join = await Client.PostAsJsonAsync($"/api/invites/{invite.Code}/join", new { });
        join.StatusCode.Should().Be(HttpStatusCode.OK);

        Auth(ownerToken);
        var list = await Client.GetFromJsonAsync<List<InviteResponse>>(
            $"/api/guilds/{guildId}/invites"
        );
        list!.Single(i => i.Code == invite.Code).UseCount.Should().Be(1);
    }

    [Fact]
    public async Task Join_AlreadyMember_Returns409()
    {
        var (ownerToken, _) = await RegisterAsync("inv_owner4", "inv_owner4@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var invite = await CreateInviteAsync(ownerToken, guildId);

        // The owner is already a member.
        Auth(ownerToken);
        var join = await Client.PostAsJsonAsync($"/api/invites/{invite.Code}/join", new { });
        join.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Join_ExhaustedMaxUses_Returns410()
    {
        var (ownerToken, _) = await RegisterAsync("inv_owner5", "inv_owner5@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var invite = await CreateInviteAsync(ownerToken, guildId, new { maxUses = 1 });

        var (firstToken, _) = await RegisterAsync("inv_first5", "inv_first5@test.com");
        Auth(firstToken);
        (await Client.PostAsJsonAsync($"/api/invites/{invite.Code}/join", new { }))
            .EnsureSuccessStatusCode();

        var (secondToken, _) = await RegisterAsync("inv_second5", "inv_second5@test.com");
        Auth(secondToken);
        var join = await Client.PostAsJsonAsync($"/api/invites/{invite.Code}/join", new { });
        join.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Preview_ExpiredInvite_Returns410()
    {
        var (ownerToken, _) = await RegisterAsync("inv_owner6", "inv_owner6@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var ownerId = OwnerIdOf(guildId);

        // Seed an already-expired invite directly — deterministic, no waiting.
        using (var scope = Factory.Services.CreateScope())
        {
            var invites = scope.ServiceProvider.GetRequiredService<IGuildInviteRepository>();
            await invites.AddAsync(
                new GuildInvite
                {
                    Code = "expired6",
                    GuildId = guildId,
                    ChannelId = null,
                    CreatorId = ownerId,
                    MaxUses = null,
                    UseCount = 0,
                    ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2000,
                }
            );
            await invites.SaveChangesAsync();
        }

        var (memberToken, _) = await RegisterAsync("inv_member6", "inv_member6@test.com");
        Auth(memberToken);
        var preview = await Client.GetAsync("/api/invites/expired6");
        preview.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Delete_RemovesInvite_AndIsScopedToGuild()
    {
        var (ownerToken, _) = await RegisterAsync("inv_owner7", "inv_owner7@test.com");
        var guildId = await CreateGuildAsync(ownerToken);
        var invite = await CreateInviteAsync(ownerToken, guildId);

        // Deleting under a different guild's route must not work (404, not a leak).
        var (otherToken, _) = await RegisterAsync("inv_owner7b", "inv_owner7b@test.com");
        var otherGuildId = await CreateGuildAsync(otherToken);
        Auth(otherToken);
        (await Client.DeleteAsync($"/api/guilds/{otherGuildId}/invites/{invite.Code}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        // The real owner deletes it.
        Auth(ownerToken);
        (await Client.DeleteAsync($"/api/guilds/{guildId}/invites/{invite.Code}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        // Now the code is dead.
        var (memberToken, _) = await RegisterAsync("inv_member7", "inv_member7@test.com");
        Auth(memberToken);
        (await Client.GetAsync($"/api/invites/{invite.Code}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record GuildResponse(long Id, string Name);

    private record InviteResponse(
        string Code,
        long GuildId,
        long? ChannelId,
        long CreatorId,
        string? CreatorUsername,
        int? MaxUses,
        int UseCount,
        long? ExpiresAt,
        long CreatedAt
    );

    private record InvitePreviewResponse(
        string Code,
        long GuildId,
        string GuildName,
        int MemberCount,
        long? ChannelId
    );
}
