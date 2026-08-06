using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Xunit;

namespace Harmony.IntegrationTests.Guilds;

/// <summary>
/// Soft-delete + Trash/restore for channels and guilds (§5.71 #5): a delete tombstones instead of
/// hard-deleting, the entity vanishes from normal reads but lists in Trash, restore brings it back,
/// and permanent-delete only works on an already-trashed entity. Also covers the guild-delete leak
/// closure — a soft-deleted guild's channels become inaccessible even by direct id.
/// </summary>
public class SoftDeleteTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public SoftDeleteTests(HarmonyWebApplicationFactory factory)
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

    private async Task<GuildDto> CreateGuildAsync(string name)
    {
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<GuildDto>())!;
    }

    private async Task<ChannelDto> CreateChannelAsync(long guildId, string name)
    {
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name, type = "text" }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ChannelDto>())!;
    }

    // ---- channels -----------------------------------------------------------

    [Fact]
    public async Task Channel_Delete_HidesFromList_ListsInTrash_ThenRestoreBringsItBack()
    {
        Auth(await RegisterAsync("sd_ch1", "sd_ch1@test.com"));
        var guild = await CreateGuildAsync("Chan SoftDelete");
        var channel = await CreateChannelAsync(guild.Id, "doomed");

        (await Client.DeleteAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        // Gone from the normal channel list…
        var live = await Client.GetFromJsonAsync<List<ChannelDto>>(
            $"/api/guilds/{guild.Id}/channels"
        );
        live!.Should().NotContain(c => c.Id == channel.Id);

        // …but present in Trash.
        var trash = await Client.GetFromJsonAsync<List<DeletedChannelDto>>(
            $"/api/guilds/{guild.Id}/channels/trash"
        );
        trash!.Should().ContainSingle(c => c.Id == channel.Id).Which.DeletedAt.Should().NotBeNull();

        // Restore → back in the live list, out of Trash.
        (await Client.PostAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}/restore", null))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        var afterRestore = await Client.GetFromJsonAsync<List<ChannelDto>>(
            $"/api/guilds/{guild.Id}/channels"
        );
        afterRestore!.Should().Contain(c => c.Id == channel.Id);
    }

    [Fact]
    public async Task Channel_PermanentDelete_OnlyWorksOnATrashedChannel()
    {
        Auth(await RegisterAsync("sd_ch2", "sd_ch2@test.com"));
        var guild = await CreateGuildAsync("Chan Permanent");
        var channel = await CreateChannelAsync(guild.Id, "doomed");

        // A live channel can't be permanently deleted — it must be trashed first.
        (await Client.DeleteAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}/permanent"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        (await Client.DeleteAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}"))
            .EnsureSuccessStatusCode();

        (await Client.DeleteAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}/permanent"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        // Now truly gone — no longer even in Trash.
        var trash = await Client.GetFromJsonAsync<List<DeletedChannelDto>>(
            $"/api/guilds/{guild.Id}/channels/trash"
        );
        trash!.Should().NotContain(c => c.Id == channel.Id);
    }

    // ---- guilds -------------------------------------------------------------

    [Fact]
    public async Task Guild_Delete_Hides_ListsInOwnerTrash_ThenRestore()
    {
        Auth(await RegisterAsync("sd_g1", "sd_g1@test.com"));
        var guild = await CreateGuildAsync("Doomed Server");

        (await Client.DeleteAsync($"/api/guilds/{guild.Id}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        // 404s everywhere…
        (await Client.GetAsync($"/api/guilds/{guild.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // …but the owner sees it in their Trash.
        var trash = await Client.GetFromJsonAsync<List<DeletedGuildDto>>("/api/guilds/trash");
        trash!.Should().ContainSingle(g => g.Id == guild.Id);

        // Restore → reachable again.
        (await Client.PostAsync($"/api/guilds/{guild.Id}/restore", null))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await Client.GetAsync($"/api/guilds/{guild.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Guild_Delete_MakesItsChannelsInaccessible_EvenByDirectId()
    {
        Auth(await RegisterAsync("sd_g2", "sd_g2@test.com"));
        var guild = await CreateGuildAsync("Server With Channel");
        var channel = await CreateChannelAsync(guild.Id, "general2");

        // Reachable while the guild is live.
        (await Client.GetAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        (await Client.DeleteAsync($"/api/guilds/{guild.Id}")).EnsureSuccessStatusCode();

        // The channel row still has DeletedAt == null, but its guild is trashed → hidden.
        (await Client.GetAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        // Restore the guild → the channel is reachable again (no cascade stamping needed).
        (await Client.PostAsync($"/api/guilds/{guild.Id}/restore", null)).EnsureSuccessStatusCode();
        (await Client.GetAsync($"/api/guilds/{guild.Id}/channels/{channel.Id}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Guild_Restore_IsOwnerOnly()
    {
        Auth(await RegisterAsync("sd_g3_owner", "sd_g3o@test.com"));
        var guild = await CreateGuildAsync("Owner Only Restore");
        (await Client.DeleteAsync($"/api/guilds/{guild.Id}")).EnsureSuccessStatusCode();

        // A different user can't restore someone else's trashed guild (owner-gated → 403), and it
        // never shows up in their own Trash list either.
        Auth(await RegisterAsync("sd_g3_other", "sd_g3x@test.com"));
        (await Client.PostAsync($"/api/guilds/{guild.Id}/restore", null))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);

        var otherTrash = await Client.GetFromJsonAsync<List<DeletedGuildDto>>("/api/guilds/trash");
        otherTrash!.Should().NotContain(g => g.Id == guild.Id);
    }

    private record AuthResponse(string AccessToken);

    private record GuildDto(long Id, string Name);

    private record ChannelDto(long Id, long? GuildId, string Name, string Type);

    private record DeletedChannelDto(long Id, long? GuildId, string Name, string Type, long? DeletedAt);

    private record DeletedGuildDto(long Id, string Name, string? IconKey, long? DeletedAt);
}
