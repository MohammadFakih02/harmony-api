using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Infrastructure.Postgres;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Notifications;

/// <summary>
/// The transactional push outbox, through the real pipeline: a mention lands a PushOutbox
/// row atomically with its Notification row, and a DM message stages one "dm" fan-out row
/// from the consumer. The dispatcher is a !isTest-gated hosted service, so staged rows
/// stay observable here instead of being drained. (Actual web-push delivery can't be
/// integration-tested — there is no browser push service in CI.)
/// </summary>
public class PushOutboxTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public PushOutboxTests(HarmonyWebApplicationFactory factory)
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

    private async Task<List<PushOutboxMessage>> OutboxRowsAsync(string kind)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
        return await db.PushOutbox.AsNoTracking().Where(m => m.Kind == kind).ToListAsync();
    }

    [Fact]
    public async Task Mention_ThroughThePipeline_StagesAnOutboxRow_WithTheNotificationRow()
    {
        var (ownerToken, ownerId) = await RegisterAsync("outbox_o1", "outbox_o1@test.com");
        Authorize(ownerToken);
        var g = await Client.PostAsJsonAsync("/api/guilds", new { name = "Outbox Guild" });
        g.EnsureSuccessStatusCode();
        var guild = await g.Content.ReadFromJsonAsync<GuildResponse>();
        var c = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild!.Id}/channels",
            new { name = "general2", type = "text" }
        );
        c.EnsureSuccessStatusCode();
        var channel = await c.Content.ReadFromJsonAsync<IdResponse>();

        var (memberToken, memberId) = await RegisterAsync("outbox_m1", "outbox_m1@test.com");
        var inviteCode = await CreateInviteCodeAsync(guild.Id);
        Authorize(memberToken);
        (
            await Client.PostAsJsonAsync($"/api/invites/{inviteCode}/join", new { })
        ).EnsureSuccessStatusCode();

        Authorize(ownerToken);
        var send = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild.Id}/channels/{channel!.Id}/messages",
            new { content = "ping @outbox_m1" }
        );
        send.EnsureSuccessStatusCode();
        var sent = await send.Content.ReadFromJsonAsync<SendMessageResponse>();

        var rows = await Eventually.GetAsync(
            action: () => OutboxRowsAsync("mention"),
            predicate: r => r.Any(m => m.RecipientId == memberId),
            retries: 100,
            intervalMs: 100
        );

        var row = rows.Single(m => m.RecipientId == memberId);
        row.ActorId.Should().Be(ownerId);
        row.GuildId.Should().Be(guild.Id);
        row.ChannelId.Should().Be(channel.Id);
        row.MessageId.Should().Be(sent!.MessageId);
        row.Attempts.Should().Be(0);

        // Atomicity witness: the Notification row committed in the same save.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
        (
            await db
                .Notifications.AsNoTracking()
                .AnyAsync(n =>
                    n.UserId == memberId && n.Type == "mention" && n.MessageId == sent.MessageId
                )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task DmMessage_ThroughTheConsumer_StagesOneDmFanOutRow()
    {
        var (aliceToken, aliceId) = await RegisterAsync("outbox_dm1", "outbox_dm1@test.com");
        var (_, bobId) = await RegisterAsync("outbox_dm2", "outbox_dm2@test.com");

        Authorize(aliceToken);
        var open = await Client.PostAsJsonAsync("/api/dm", new { targetUserId = bobId });
        open.EnsureSuccessStatusCode();
        var dm = await open.Content.ReadFromJsonAsync<DmDto>();

        var send = await Client.PostAsJsonAsync(
            $"/api/dm/{dm!.ChannelId}/messages",
            new { content = "psst, you offline?" }
        );
        send.EnsureSuccessStatusCode();
        var sent = await send.Content.ReadFromJsonAsync<SendMessageResponse>();

        var rows = await Eventually.GetAsync(
            action: () => OutboxRowsAsync("dm"),
            predicate: r => r.Any(m => m.MessageId == sent!.MessageId),
            retries: 100,
            intervalMs: 100
        );

        var row = rows.Single(m => m.MessageId == sent!.MessageId);
        row.ActorId.Should().Be(aliceId);
        row.ChannelId.Should().Be(dm.ChannelId);
        row.RecipientId.Should().Be(0); // fan-out resolved at dispatch time
        row.GuildId.Should().BeNull();
    }

    private sealed record DmDto(long ChannelId);

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record GuildResponse(long Id, string Name);

    private record IdResponse(long Id);

    private record SendMessageResponse(long MessageId);
}
