using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Messages;

/// <summary>
/// <see cref="IMessageReactionRepository"/> against real Postgres (raw ON CONFLICT insert + grouped
/// aggregation don't translate on an in-memory provider). Covers idempotent add, remove, and the
/// page-summary grouping (per-emoji count + per-viewer meReacted, stable first-reaction order).
/// message_id/channel_id are FK-less snowflakes; user_id needs real Users rows, minted via register.
/// </summary>
public class MessageReactionRepositoryTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public MessageReactionRepositoryTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private async Task<long> RegisterUserAsync(string username, string email)
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, email, password = "Password123!" }
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.User.Id;
    }

    private async Task<T> WithRepoAsync<T>(Func<IMessageReactionRepository, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageReactionRepository>();
        return await work(repo);
    }

    private async Task WithRepoAsync(Func<IMessageReactionRepository, Task> work) =>
        await WithRepoAsync<object?>(async r =>
        {
            await work(r);
            return null;
        });

    [Fact]
    public async Task Add_IsIdempotent_AndSummaryCountsDistinctUsers()
    {
        var u1 = await RegisterUserAsync("rr_add1", "rr_add1@test.com");
        var u2 = await RegisterUserAsync("rr_add2", "rr_add2@test.com");
        long messageId = 900_000_001;
        long channelId = 800_000_001;
        const string emoji = "😀";

        await WithRepoAsync(async repo =>
        {
            await repo.AddAsync(messageId, channelId, emoji, u1, 1, default);
            await repo.AddAsync(messageId, channelId, emoji, u1, 2, default); // duplicate → no-op
            await repo.AddAsync(messageId, channelId, emoji, u2, 3, default);
        });

        var summary = await WithRepoAsync(repo =>
            repo.GetSummariesAsync(new[] { messageId }, u1, default));

        summary.Should().ContainKey(messageId);
        var bucket = summary[messageId].Single(x => x.Emoji == emoji);
        bucket.Count.Should().Be(2); // two distinct users, the duplicate collapsed
        bucket.MeReacted.Should().BeTrue(); // viewer u1 reacted
    }

    [Fact]
    public async Task Remove_DropsOnlyThatUsersReaction()
    {
        var u1 = await RegisterUserAsync("rr_rem1", "rr_rem1@test.com");
        var u2 = await RegisterUserAsync("rr_rem2", "rr_rem2@test.com");
        long messageId = 900_000_002;
        long channelId = 800_000_002;
        const string emoji = "🔥";

        await WithRepoAsync(async repo =>
        {
            await repo.AddAsync(messageId, channelId, emoji, u1, 1, default);
            await repo.AddAsync(messageId, channelId, emoji, u2, 2, default);
            await repo.RemoveAsync(messageId, emoji, u1, default);
        });

        var summary = await WithRepoAsync(repo =>
            repo.GetSummariesAsync(new[] { messageId }, u1, default));

        var bucket = summary[messageId].Single(x => x.Emoji == emoji);
        bucket.Count.Should().Be(1); // only u2 remains
        bucket.MeReacted.Should().BeFalse(); // u1 removed theirs
    }

    [Fact]
    public async Task GetSummaries_OrdersEmojisByFirstReactionTime()
    {
        var u1 = await RegisterUserAsync("rr_ord1", "rr_ord1@test.com");
        long messageId = 900_000_003;
        long channelId = 800_000_003;

        await WithRepoAsync(async repo =>
        {
            await repo.AddAsync(messageId, channelId, "🥈", u1, 20, default);
            await repo.AddAsync(messageId, channelId, "🥇", u1, 10, default); // earlier
        });

        var summary = await WithRepoAsync(repo =>
            repo.GetSummariesAsync(new[] { messageId }, u1, default));

        summary[messageId].Select(x => x.Emoji).Should().Equal("🥇", "🥈");
    }

    [Fact]
    public async Task GetSummaries_OmitsMessagesWithNoReactions()
    {
        var summary = await WithRepoAsync(repo =>
            repo.GetSummariesAsync(new[] { 777_000_001L, 777_000_002L }, 1, default));
        summary.Should().BeEmpty();
    }

    private record AuthResponse(UserDto User);
    private record UserDto(long Id);
}
