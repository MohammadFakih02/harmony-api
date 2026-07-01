using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Messages;

/// <summary>
/// Full-text message search (flow #26) against the real Postgres FTS read model that the
/// SearchIndexConsumer maintains: a match is found and enriched (sender + channel name), the
/// channel filter narrows results, an empty query is a no-op, a non-member is refused, and — the
/// security-critical case — a hit in a channel the caller cannot ViewChannel is excluded.
/// </summary>
public class SearchTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public SearchTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void Auth(string token) =>
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

    private async Task<(long guildId, string invite)> CreateGuildAsync(string token)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Search Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<IdDto>();
        return (guild!.Id, await CreateInviteCodeAsync(guild.Id));
    }

    private async Task<long> CreateChannelAsync(string token, long guildId, string name)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name, type = "text", position = 0 }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private async Task<long> SendAsync(long guildId, long channelId, string content)
    {
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SendDto>())!.MessageId;
    }

    private async Task<long> JoinAsync(string token, string invite)
    {
        Auth(token);
        var resp = await Client.PostAsJsonAsync($"/api/invites/{invite}/join", new { });
        resp.EnsureSuccessStatusCode();
        return 0;
    }

    private async Task<SearchResults> SearchAsync(
        string token,
        long guildId,
        string query,
        long? channelId = null
    )
    {
        Auth(token);
        var url = $"/api/guilds/{guildId}/search?q={Uri.EscapeDataString(query)}";
        if (channelId is { } cid)
            url += $"&channelId={cid}";
        var resp = await Client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SearchResults>())!;
    }

    // Polls until the async SearchIndexConsumer has indexed a matching message.
    private Task<SearchResults> SearchUntilFoundAsync(
        string token,
        long guildId,
        string query,
        long messageId,
        long? channelId = null
    ) =>
        Eventually.GetAsync(
            action: () => SearchAsync(token, guildId, query, channelId),
            predicate: r => r.Results.Any(x => x.MessageId == messageId),
            retries: 100,
            intervalMs: 100
        );

    private static long EveryoneRoleId(HarmonyWebApplicationFactory factory, long guildId)
    {
        using var scope = factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        return roles.GetDefaultRoleAsync(guildId).GetAwaiter().GetResult()!.Id;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Search_FindsMatchingMessage_EnrichedWithSenderAndChannel()
    {
        var (token, _) = await RegisterAsync("search_owner1", "search_owner1@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId, "general");

        Auth(token);
        var hitId = await SendAsync(guildId, channelId, "the pineapple belongs on pizza");
        await SendAsync(guildId, channelId, "completely unrelated chatter");

        var results = await SearchUntilFoundAsync(token, guildId, "pineapple", hitId);

        var hit = results.Results.Single(r => r.MessageId == hitId);
        hit.ChannelId.Should().Be(channelId);
        hit.ChannelName.Should().Be("general");
        hit.Username.Should().Be("search_owner1");
        hit.Content.Should().Contain("pineapple");
        results.Results.Should().NotContain(r => r.Content.Contains("unrelated"));
    }

    [Fact]
    public async Task Search_ChannelFilter_NarrowsToOneChannel()
    {
        var (token, _) = await RegisterAsync("search_owner2", "search_owner2@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var chanA = await CreateChannelAsync(token, guildId, "alpha");
        var chanB = await CreateChannelAsync(token, guildId, "bravo");

        Auth(token);
        var aId = await SendAsync(guildId, chanA, "shared kiwi keyword here");
        var bId = await SendAsync(guildId, chanB, "another kiwi mention over here");

        // Wait until both are indexed (unfiltered), then assert the channel filter.
        await Eventually.GetAsync(
            action: () => SearchAsync(token, guildId, "kiwi"),
            predicate: r =>
                r.Results.Any(x => x.MessageId == aId) && r.Results.Any(x => x.MessageId == bId),
            retries: 100,
            intervalMs: 100
        );

        var filtered = await SearchAsync(token, guildId, "kiwi", channelId: chanA);
        filtered.Results.Should().Contain(r => r.MessageId == aId);
        filtered.Results.Should().NotContain(r => r.MessageId == bId);
    }

    [Fact]
    public async Task Search_ExcludesHitsInChannelsTheCallerCannotView()
    {
        var (ownerToken, _) = await RegisterAsync("search_owner3", "search_owner3@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var staffId = await CreateChannelAsync(ownerToken, guildId, "staff");
        var everyoneId = EveryoneRoleId(Factory, guildId);

        // Deny ViewChannel to @everyone on #staff (the classic hidden-channel pattern).
        Auth(ownerToken);
        var deny = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{staffId}/overrides/{everyoneId}",
            new { targetType = "role", allowBits = 0L, denyBits = (long)Permission.ViewChannel }
        );
        deny.EnsureSuccessStatusCode();

        var secretId = await SendAsync(guildId, staffId, "the banana launch codes are secret");

        // The owner bypasses overrides → proves the message really was indexed.
        await SearchUntilFoundAsync(ownerToken, guildId, "banana", secretId);

        // A plain member who cannot view #staff must not see the hit.
        var (memberToken, _) = await RegisterAsync("search_member3", "search_member3@test.com");
        await JoinAsync(memberToken, invite);

        var memberView = await SearchAsync(memberToken, guildId, "banana");
        memberView.Results.Should().NotContain(r => r.MessageId == secretId);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        var (token, _) = await RegisterAsync("search_owner4", "search_owner4@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId, "general");
        Auth(token);
        await SendAsync(guildId, channelId, "some searchable content");

        var results = await SearchAsync(token, guildId, "   ");
        results.Results.Should().BeEmpty();
        results.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Search_NonMember_IsForbidden()
    {
        var (ownerToken, _) = await RegisterAsync("search_owner5", "search_owner5@test.com");
        var (guildId, _) = await CreateGuildAsync(ownerToken);

        var (outsiderToken, _) = await RegisterAsync("search_outsider5", "search_outsider5@test.com");
        Auth(outsiderToken);
        var resp = await Client.GetAsync($"/api/guilds/{guildId}/search?q=anything");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------------------------
    // DTOs
    // -------------------------------------------------------------------------

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record IdDto(long Id);

    private record SendDto(long MessageId);

    private record SearchResults(List<SearchResult> Results, bool HasMore);

    private record SearchResult(
        long MessageId,
        long ChannelId,
        string ChannelName,
        long UserId,
        string Username,
        string Content
    );
}
