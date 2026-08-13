using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Hubs;

/// <summary>
/// End-to-end unread-count flow against real Redis (the D3 read-time model):
///   REST send by A → RabbitMQ → ScyllaMessageConsumer → INCR channel:{ch}:count
///   → ONE Clients.Group(guild).ChannelActivity ping (no per-recipient fan-out).
///   Unread is read-time: GET /me/unread = channel-count − the caller's mark.
///
/// Proves: guild members in the guild group get the live ChannelActivity ping; the read path
/// surfaces count − mark; and mark-as-read anchors the mark (→ 0, cleared from GET /me/unread)
/// and still pushes the zero UnreadCountUpdated for multi-device sync. Requires real Redis.
/// (The sender also receives the guild-group ping; skipping own messages is a client concern.)
/// </summary>
public class UnreadCountFlowTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public UnreadCountFlowTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    private HubConnection BuildConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, $"hubs/chat?access_token={accessToken}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                }
            )
            // The server serializes every Snowflake long as a JSON string (LongStringConverter);
            // mirror that on the client so string ids deserialize back into long DTO fields.
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString
            )
            .Build();

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

    private async Task<(long guildId, long channelId, string inviteCode)> SetupGuildAsync(
        string ownerToken
    )
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ownerToken);

        var g = await Client.PostAsJsonAsync("/api/guilds", new { name = "Unread Guild" });
        g.EnsureSuccessStatusCode();
        var guild = await g.Content.ReadFromJsonAsync<GuildResponse>();

        var c = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild!.Id}/channels",
            new { name = "general", type = "text" }
        );
        c.EnsureSuccessStatusCode();
        var channel = await c.Content.ReadFromJsonAsync<IdResponse>();

        // §5.31 posts a `member_join` system message on every guild join, authored by the joining
        // member — which legitimately increments the OWNER's unread and races the owner's SignalR
        // connect (the message may be broadcast before or after ownerConn.StartAsync()). That confound
        // is what made SendMessage_..._NotToSender flaky. Suppress system messages so these tests
        // isolate the unread fan-out they actually exercise.
        var w = await Client.PatchAsync(
            $"/api/guilds/{guild.Id}/welcome",
            JsonContent.Create(
                new
                {
                    welcomeChannelId = (long?)null,
                    welcomeMessage = (string?)null,
                    systemMessagesEnabled = false,
                }
            )
        );
        w.EnsureSuccessStatusCode();

        return (guild.Id, channel!.Id, await CreateInviteCodeAsync(guild.Id));
    }

    private async Task JoinGuildAsync(string token, string inviteCode)
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await Client.PostAsJsonAsync($"/api/invites/{inviteCode}/join", new { });
        resp.EnsureSuccessStatusCode();
    }

    private async Task SendMessageAsync(string token, long guildId, long channelId, string content)
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content }
        );
        resp.EnsureSuccessStatusCode();
    }

    private async Task<List<UnreadCountResponseDto>> GetUnreadAsync(string token)
    {
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await Client.GetAsync("/api/users/me/unread");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<UnreadCountResponseDto>>()) ?? [];
    }

    [Fact]
    public async Task SendMessage_ShouldPushChannelActivityToGuildGroup_WithAuthor()
    {
        var (ownerToken, ownerId) = await RegisterAsync("unreadowner", "u-owner@test.com");
        var (guildId, channelId, invite) = await SetupGuildAsync(ownerToken);

        var (memberToken, _) = await RegisterAsync("unreadmember", "u-member@test.com");
        await JoinGuildAsync(memberToken, invite);

        var memberConn = BuildConnection(memberToken);
        var activity = new List<ChannelActivityPayload>();
        memberConn.On<ChannelActivityPayload>("ChannelActivity", p => activity.Add(p));
        await memberConn.StartAsync();
        // Members receive guild-scoped broadcasts only while joined to the guild group — the real
        // client does this for every guild on connect (shell.joinAllGuilds).
        await memberConn.InvokeAsync("JoinGuild", guildId);

        try
        {
            await SendMessageAsync(ownerToken, guildId, channelId, "first");

            // One guild-group ping identifying the channel + author (the client self-skips its own).
            await Eventually.GetAsync(
                action: () => Task.FromResult(activity),
                predicate: a => a.Any(p => p.ChannelId == channelId && p.AuthorId == ownerId),
                retries: 100,
                intervalMs: 100
            );

            activity
                .Should()
                .ContainSingle(p =>
                    p.ChannelId == channelId && p.GuildId == guildId && p.AuthorId == ownerId
                );
        }
        finally
        {
            await memberConn.StopAsync();
            await memberConn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadPath_ShouldReportCountMinusMark_AfterAnchoring()
    {
        var (ownerToken, _) = await RegisterAsync("unreadowner2", "u-owner2@test.com");
        var (guildId, channelId, invite) = await SetupGuildAsync(ownerToken);
        var (memberToken, _) = await RegisterAsync("unreadmember2", "u-member2@test.com");
        await JoinGuildAsync(memberToken, invite);

        // Anchor the member's mark at the current (empty) count, so subsequent messages are unread.
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", memberToken);
        var mark = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/read",
            new { lastReadMessageId = 1L }
        );
        mark.EnsureSuccessStatusCode();

        await SendMessageAsync(ownerToken, guildId, channelId, "one");
        await SendMessageAsync(ownerToken, guildId, channelId, "two");

        // The read path (count − mark) converges on 2 as the async consumer INCRs the channel counter.
        await Eventually.GetAsync(
            action: () => GetUnreadAsync(memberToken),
            predicate: list => list.Any(u => u.ChannelId == channelId && u.UnreadCount == 2),
            retries: 100,
            intervalMs: 100
        );

        (await GetUnreadAsync(memberToken))
            .Should()
            .ContainSingle(u => u.ChannelId == channelId && u.GuildId == guildId && u.UnreadCount == 2);
    }

    [Fact]
    public async Task MarkRead_ShouldAnchorToZero_ClearFromGetUnread_AndPushZero()
    {
        var (ownerToken, ownerId) = await RegisterAsync("unreadowner3", "u-owner3@test.com");
        var (guildId, channelId, invite) = await SetupGuildAsync(ownerToken);
        var (memberToken, _) = await RegisterAsync("unreadmember3", "u-member3@test.com");
        await JoinGuildAsync(memberToken, invite);

        var memberConn = BuildConnection(memberToken);
        var memberUnread = new List<UnreadCountPayload>();
        var activity = new List<ChannelActivityPayload>();
        memberConn.On<UnreadCountPayload>("UnreadCountUpdated", p => memberUnread.Add(p));
        memberConn.On<ChannelActivityPayload>("ChannelActivity", p => activity.Add(p));
        await memberConn.StartAsync();
        await memberConn.InvokeAsync("JoinGuild", guildId);

        try
        {
            await SendMessageAsync(ownerToken, guildId, channelId, "unread me");

            // The ping fires AFTER the counter INCR, so receiving it proves the count is ≥ 1 —
            // marking read now anchors the mark to that count (→ 0), not to an empty channel.
            await Eventually.GetAsync(
                action: () => Task.FromResult(activity),
                predicate: a => a.Any(p => p.ChannelId == channelId && p.AuthorId == ownerId),
                retries: 100,
                intervalMs: 100
            );

            Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", memberToken);
            var markResp = await Client.PostAsJsonAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/read",
                new { lastReadMessageId = 999999L }
            );
            markResp.EnsureSuccessStatusCode();

            // A zero push still arrives (multi-device sync).
            await Eventually.GetAsync(
                action: () => Task.FromResult(memberUnread),
                predicate: u => u.Any(p => p.ChannelId == channelId && p.UnreadCount == 0),
                retries: 100,
                intervalMs: 100
            );

            // And the read path (count − mark) no longer lists the channel.
            (await GetUnreadAsync(memberToken)).Should().NotContain(u => u.ChannelId == channelId);
        }
        finally
        {
            await memberConn.StopAsync();
            await memberConn.DisposeAsync();
        }
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record GuildResponse(long Id, string Name);

    private record IdResponse(long Id);

    private record UnreadCountResponseDto(long ChannelId, long GuildId, int UnreadCount);
}
