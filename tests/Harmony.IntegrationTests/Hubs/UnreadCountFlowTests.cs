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
/// End-to-end unread-count flow against real Redis:
///   REST send by A → RabbitMQ → ScyllaMessageConsumer → INCR unread:{B}:{channel}
///   → Clients.User(B).UnreadCountUpdated.
///
/// Proves: recipients get the push with the correct absolute count, the SENDER
/// does not, mark-as-read resets to zero, and GET /me/unread reflects Redis state.
/// Requires real Redis (factory now supplies it).
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

    [Fact]
    public async Task SendMessage_ShouldPushUnreadCountToRecipient_NotToSender()
    {
        var (ownerToken, _) = await RegisterAsync("unreadowner", "u-owner@test.com");
        var (guildId, channelId, invite) = await SetupGuildAsync(ownerToken);

        var (memberToken, _) = await RegisterAsync("unreadmember", "u-member@test.com");
        await JoinGuildAsync(memberToken, invite);

        var ownerConn = BuildConnection(ownerToken);
        var memberConn = BuildConnection(memberToken);

        var ownerUnread = new List<UnreadCountPayload>();
        var memberUnread = new List<UnreadCountPayload>();
        ownerConn.On<UnreadCountPayload>("UnreadCountUpdated", p => ownerUnread.Add(p));
        memberConn.On<UnreadCountPayload>("UnreadCountUpdated", p => memberUnread.Add(p));

        await ownerConn.StartAsync();
        await memberConn.StartAsync();

        try
        {
            // Owner sends — member is the recipient, owner is the sender.
            await SendMessageAsync(ownerToken, guildId, channelId, "first");

            // Member should receive an absolute count of 1.
            await Eventually.GetAsync(
                action: () => Task.FromResult(memberUnread),
                predicate: u => u.Any(p => p.ChannelId == channelId && p.UnreadCount == 1),
                retries: 100,
                intervalMs: 100
            );

            memberUnread
                .Should()
                .ContainSingle(p =>
                    p.ChannelId == channelId && p.GuildId == guildId && p.UnreadCount == 1
                );

            // Sender must NOT get an unread push for their own message.
            await Task.Delay(500);
            ownerUnread.Should().BeEmpty("the sender is excluded from their own unread fan-out");
        }
        finally
        {
            await ownerConn.StopAsync();
            await memberConn.StopAsync();
            await ownerConn.DisposeAsync();
            await memberConn.DisposeAsync();
        }
    }

    [Fact]
    public async Task SecondMessage_ShouldIncrementRecipientCount_ToTwo()
    {
        var (ownerToken, _) = await RegisterAsync("unreadowner2", "u-owner2@test.com");
        var (guildId, channelId, invite) = await SetupGuildAsync(ownerToken);
        var (memberToken, _) = await RegisterAsync("unreadmember2", "u-member2@test.com");
        await JoinGuildAsync(memberToken, invite);

        var memberConn = BuildConnection(memberToken);
        var memberUnread = new List<UnreadCountPayload>();
        memberConn.On<UnreadCountPayload>("UnreadCountUpdated", p => memberUnread.Add(p));
        await memberConn.StartAsync();

        try
        {
            await SendMessageAsync(ownerToken, guildId, channelId, "one");
            await SendMessageAsync(ownerToken, guildId, channelId, "two");

            await Eventually.GetAsync(
                action: () => Task.FromResult(memberUnread),
                predicate: u => u.Any(p => p.ChannelId == channelId && p.UnreadCount == 2),
                retries: 100,
                intervalMs: 100
            );

            memberUnread.Should().Contain(p => p.UnreadCount == 2);
        }
        finally
        {
            await memberConn.StopAsync();
            await memberConn.DisposeAsync();
        }
    }

    [Fact]
    public async Task MarkRead_ShouldResetCountToZero_AndClearFromGetUnread()
    {
        var (ownerToken, _) = await RegisterAsync("unreadowner3", "u-owner3@test.com");
        var (guildId, channelId, invite) = await SetupGuildAsync(ownerToken);
        var (memberToken, _) = await RegisterAsync("unreadmember3", "u-member3@test.com");
        await JoinGuildAsync(memberToken, invite);

        var memberConn = BuildConnection(memberToken);
        var memberUnread = new List<UnreadCountPayload>();
        memberConn.On<UnreadCountPayload>("UnreadCountUpdated", p => memberUnread.Add(p));
        await memberConn.StartAsync();

        try
        {
            await SendMessageAsync(ownerToken, guildId, channelId, "unread me");

            await Eventually.GetAsync(
                action: () => Task.FromResult(memberUnread),
                predicate: u => u.Any(p => p.UnreadCount == 1),
                retries: 100,
                intervalMs: 100
            );

            // Member marks the channel read.
            Client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", memberToken);
            var markResp = await Client.PostAsJsonAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/read",
                new { lastReadMessageId = 999999L }
            );
            markResp.EnsureSuccessStatusCode();

            // A zero push should arrive (multi-device sync).
            await Eventually.GetAsync(
                action: () => Task.FromResult(memberUnread),
                predicate: u => u.Any(p => p.ChannelId == channelId && p.UnreadCount == 0),
                retries: 100,
                intervalMs: 100
            );

            // GET /me/unread should no longer list this channel (key deleted).
            var unreadResp = await Client.GetAsync("/api/users/me/unread");
            unreadResp.EnsureSuccessStatusCode();
            var list = await unreadResp.Content.ReadFromJsonAsync<List<UnreadCountResponseDto>>();
            list.Should().NotContain(u => u.ChannelId == channelId);
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
