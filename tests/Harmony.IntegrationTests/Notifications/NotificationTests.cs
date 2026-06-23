using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.Application.Hubs;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Notifications;

/// <summary>
/// REST + end-to-end tests for notifications-system: a friend request and a mention
/// each create a persisted Notification row and push a live NotificationReceived event
/// to the owner; the mute/preference suppression chain (block is exercised in
/// FriendTests/UserMuteTests already) skips the row entirely; list/unread-count/
/// mark-read/mark-all-read round-trip against the persisted rows.
/// </summary>
public class NotificationTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public NotificationTests(HarmonyWebApplicationFactory factory)
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

    private void Authorize(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private Task<HttpResponseMessage> SendFriendRequestAsync(string username) =>
        Client.PostAsJsonAsync("/api/friends/request", new { username });

    [Fact]
    public async Task FriendRequest_CreatesNotification_AndPushesLive()
    {
        var (tokenA, idA) = await RegisterAsync("notif_a1", "notif_a1@test.com");
        var (tokenB, idB) = await RegisterAsync("notif_b1", "notif_b1@test.com");

        var connB = BuildConnection(tokenB);
        var received = new List<NotificationPayload>();
        connB.On<NotificationPayload>("NotificationReceived", p => received.Add(p));
        await connB.StartAsync();

        try
        {
            Authorize(tokenA);
            (await SendFriendRequestAsync("notif_b1")).EnsureSuccessStatusCode();

            await Eventually.GetAsync(
                action: () => Task.FromResult(received),
                predicate: r => r.Any(p => p.Type == "friend_request" && p.ActorId == idA),
                retries: 100,
                intervalMs: 100
            );

            Authorize(tokenB);
            var list = await Client.GetFromJsonAsync<List<NotificationDto>>("/api/notifications");
            list.Should().ContainSingle(n => n.Type == "friend_request" && n.ActorId == idA);

            var unreadCount = await Client.GetFromJsonAsync<int>(
                "/api/notifications/unread-count"
            );
            unreadCount.Should().Be(1);
        }
        finally
        {
            await connB.StopAsync();
            await connB.DisposeAsync();
        }
    }

    [Fact]
    public async Task FriendRequest_WhenAddresseeMutesRequester_SuppressesNotification()
    {
        var (tokenA, idA) = await RegisterAsync("notif_a2", "notif_a2@test.com");
        var (tokenB, _) = await RegisterAsync("notif_b2", "notif_b2@test.com");

        // B mutes A (as a user) before A's request ever arrives.
        Authorize(tokenB);
        (
            await Client.PostAsJsonAsync(
                "/api/mutes",
                new
                {
                    targetType = "user",
                    targetId = idA,
                    mutedUntil = (long?)null,
                }
            )
        ).EnsureSuccessStatusCode();

        Authorize(tokenA);
        // The friend request itself must still succeed — muting only suppresses the
        // notification, not the relationship.
        (await SendFriendRequestAsync("notif_b2")).EnsureSuccessStatusCode();

        Authorize(tokenB);
        // No Eventually here on purpose — we're proving absence. Give the (synchronous,
        // same-request) notification path time to have run, then assert it never created one.
        await Task.Delay(300);
        var unreadCount = await Client.GetFromJsonAsync<int>("/api/notifications/unread-count");
        unreadCount.Should().Be(0);
    }

    [Fact]
    public async Task MarkAsRead_SetsIsReadTrue_AndDecrementsUnreadCount()
    {
        var (tokenA, idA) = await RegisterAsync("notif_a3", "notif_a3@test.com");
        var (tokenB, _) = await RegisterAsync("notif_b3", "notif_b3@test.com");

        Authorize(tokenA);
        (await SendFriendRequestAsync("notif_b3")).EnsureSuccessStatusCode();

        Authorize(tokenB);
        var notification = await Eventually.GetAsync(
            action: async () =>
                (await Client.GetFromJsonAsync<List<NotificationDto>>("/api/notifications"))!,
            predicate: list => list.Any(n => n.ActorId == idA),
            retries: 100,
            intervalMs: 100
        );
        var id = notification.Single(n => n.ActorId == idA).Id;

        var markResp = await Client.PatchAsync($"/api/notifications/{id}/read", null);
        markResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Client.GetFromJsonAsync<List<NotificationDto>>("/api/notifications");
        list.Should().ContainSingle(n => n.Id == id && n.IsRead);

        var unreadCount = await Client.GetFromJsonAsync<int>("/api/notifications/unread-count");
        unreadCount.Should().Be(0);
    }

    [Fact]
    public async Task MarkAllAsRead_ClearsUnreadCount_ForEveryNotification()
    {
        var (tokenA, _) = await RegisterAsync("notif_a4", "notif_a4@test.com");
        var (tokenC, _) = await RegisterAsync("notif_c4", "notif_c4@test.com");
        var (tokenB, _) = await RegisterAsync("notif_b4", "notif_b4@test.com");

        Authorize(tokenA);
        (await SendFriendRequestAsync("notif_b4")).EnsureSuccessStatusCode();
        Authorize(tokenC);
        (await SendFriendRequestAsync("notif_b4")).EnsureSuccessStatusCode();

        Authorize(tokenB);
        await Eventually.GetAsync(
            action: async () => await Client.GetFromJsonAsync<int>("/api/notifications/unread-count"),
            predicate: count => count == 2,
            retries: 100,
            intervalMs: 100
        );

        var readAllResp = await Client.PostAsync("/api/notifications/read-all", null);
        readAllResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var unreadCount = await Client.GetFromJsonAsync<int>("/api/notifications/unread-count");
        unreadCount.Should().Be(0);

        var list = await Client.GetFromJsonAsync<List<NotificationDto>>("/api/notifications");
        list!.Should().HaveCount(2).And.OnlyContain(n => n.IsRead);
    }

    [Fact]
    public async Task MarkAsRead_ForSomeoneElsesNotification_Returns404()
    {
        var (tokenA, idA) = await RegisterAsync("notif_a5", "notif_a5@test.com");
        var (tokenB, _) = await RegisterAsync("notif_b5", "notif_b5@test.com");

        Authorize(tokenA);
        (await SendFriendRequestAsync("notif_b5")).EnsureSuccessStatusCode();

        Authorize(tokenB);
        var notification = await Eventually.GetAsync(
            action: async () =>
                (await Client.GetFromJsonAsync<List<NotificationDto>>("/api/notifications"))!,
            predicate: list => list.Any(n => n.ActorId == idA),
            retries: 100,
            intervalMs: 100
        );
        var id = notification.Single(n => n.ActorId == idA).Id;

        // A did not receive this notification — it belongs to B.
        Authorize(tokenA);
        var resp = await Client.PatchAsync($"/api/notifications/{id}/read", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mention_ViaSentMessage_CreatesNotification_AndPushesLive()
    {
        var (ownerToken, ownerId) = await RegisterAsync("notif_owner6", "notif_owner6@test.com");
        Authorize(ownerToken);
        var g = await Client.PostAsJsonAsync("/api/guilds", new { name = "Mention Guild" });
        g.EnsureSuccessStatusCode();
        var guild = await g.Content.ReadFromJsonAsync<GuildResponse>();
        var c = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild!.Id}/channels",
            new { name = "general", type = "text" }
        );
        c.EnsureSuccessStatusCode();
        var channel = await c.Content.ReadFromJsonAsync<IdResponse>();

        var (memberToken, memberId) = await RegisterAsync(
            "notif_member6",
            "notif_member6@test.com"
        );
        Authorize(memberToken);
        (
            await Client.PostAsJsonAsync($"/api/guilds/join/{guild.InviteCode}", new { })
        ).EnsureSuccessStatusCode();

        var memberConn = BuildConnection(memberToken);
        var received = new List<NotificationPayload>();
        memberConn.On<NotificationPayload>("NotificationReceived", p => received.Add(p));
        await memberConn.StartAsync();

        try
        {
            Authorize(ownerToken);
            // Mentions are now detected server-side from the @username literal in the content —
            // there is no client-supplied mentionIds field anymore (NON-NEGOTIABLE #8).
            var send = await Client.PostAsJsonAsync(
                $"/api/guilds/{guild.Id}/channels/{channel!.Id}/messages",
                new { content = "hey @notif_member6" }
            );
            send.EnsureSuccessStatusCode();
            var sent = await send.Content.ReadFromJsonAsync<SendMessageResponse>();

            await Eventually.GetAsync(
                action: () => Task.FromResult(received),
                predicate: r => r.Any(p => p.Type == "mention" && p.ActorId == ownerId),
                retries: 100,
                intervalMs: 100
            );

            received
                .Should()
                .ContainSingle(p =>
                    p.Type == "mention"
                    && p.ActorId == ownerId
                    && p.GuildId == guild.Id
                    && p.ChannelId == channel.Id
                    && p.MessageId == sent!.MessageId
                );

            Authorize(memberToken);
            var list = await Client.GetFromJsonAsync<List<NotificationDto>>("/api/notifications");
            list.Should()
                .ContainSingle(n =>
                    n.Type == "mention" && n.ActorId == ownerId && n.MessageId == sent!.MessageId
                );
        }
        finally
        {
            await memberConn.StopAsync();
            await memberConn.DisposeAsync();
        }
    }

    [Fact]
    public async Task EveryoneMention_PushesLive_ToConnectedMember()
    {
        var (ownerToken, ownerId) = await RegisterAsync("notif_owner9", "notif_owner9@test.com");
        Authorize(ownerToken);
        var g = await Client.PostAsJsonAsync("/api/guilds", new { name = "Everyone Live Guild" });
        g.EnsureSuccessStatusCode();
        var guild = await g.Content.ReadFromJsonAsync<GuildResponse>();
        var c = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild!.Id}/channels",
            new { name = "general", type = "text" }
        );
        c.EnsureSuccessStatusCode();
        var channel = await c.Content.ReadFromJsonAsync<IdResponse>();

        var (memberToken, _) = await RegisterAsync("notif_member9", "notif_member9@test.com");
        Authorize(memberToken);
        (await Client.PostAsJsonAsync($"/api/guilds/join/{guild.InviteCode}", new { }))
            .EnsureSuccessStatusCode();

        var memberConn = BuildConnection(memberToken);
        var received = new List<NotificationPayload>();
        memberConn.On<NotificationPayload>("NotificationReceived", p => received.Add(p));
        await memberConn.StartAsync();

        try
        {
            Authorize(ownerToken);
            var send = await Client.PostAsJsonAsync(
                $"/api/guilds/{guild.Id}/channels/{channel!.Id}/messages",
                new { content = "@everyone standup!" }
            );
            send.EnsureSuccessStatusCode();

            await Eventually.GetAsync(
                action: () => Task.FromResult(received),
                predicate: r => r.Any(p => p.Type == "mention" && p.ActorId == ownerId),
                retries: 100,
                intervalMs: 100
            );

            received.Should().ContainSingle(p => p.Type == "mention" && p.ActorId == ownerId);
        }
        finally
        {
            await memberConn.StopAsync();
            await memberConn.DisposeAsync();
        }
    }

    [Fact]
    public async Task EveryoneMention_ByOwner_NotifiesAllOtherMembers()
    {
        var (ownerToken, ownerId) = await RegisterAsync("notif_owner7", "notif_owner7@test.com");
        Authorize(ownerToken);
        var g = await Client.PostAsJsonAsync("/api/guilds", new { name = "Everyone Guild" });
        g.EnsureSuccessStatusCode();
        var guild = await g.Content.ReadFromJsonAsync<GuildResponse>();
        var c = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild!.Id}/channels",
            new { name = "general", type = "text" }
        );
        c.EnsureSuccessStatusCode();
        var channel = await c.Content.ReadFromJsonAsync<IdResponse>();

        var (memberToken, memberId) = await RegisterAsync("notif_member7", "notif_member7@test.com");
        Authorize(memberToken);
        (await Client.PostAsJsonAsync($"/api/guilds/join/{guild.InviteCode}", new { }))
            .EnsureSuccessStatusCode();

        // The owner resolves to all permission bits, including MentionEveryone — @everyone expands.
        Authorize(ownerToken);
        var send = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild.Id}/channels/{channel!.Id}/messages",
            new { content = "@everyone standup time" }
        );
        send.EnsureSuccessStatusCode();

        Authorize(memberToken);
        var list = await Eventually.GetAsync(
            action: async () =>
                (await Client.GetFromJsonAsync<List<NotificationDto>>("/api/notifications"))!,
            predicate: l => l.Any(n => n.Type == "mention" && n.ActorId == ownerId),
            retries: 100,
            intervalMs: 100
        );
        list.Should().ContainSingle(n => n.Type == "mention" && n.ActorId == ownerId);
    }

    [Fact]
    public async Task EveryoneMention_ByMemberWithoutPermission_StillSends_ButDoesNotExpand()
    {
        var (ownerToken, _) = await RegisterAsync("notif_owner8", "notif_owner8@test.com");
        Authorize(ownerToken);
        var g = await Client.PostAsJsonAsync("/api/guilds", new { name = "No Everyone Guild" });
        g.EnsureSuccessStatusCode();
        var guild = await g.Content.ReadFromJsonAsync<GuildResponse>();
        var c = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild!.Id}/channels",
            new { name = "general", type = "text" }
        );
        c.EnsureSuccessStatusCode();
        var channel = await c.Content.ReadFromJsonAsync<IdResponse>();

        // A plain joined member only has the @everyone default role bits, which do NOT
        // include MentionEveryone.
        var (memberToken, memberId) = await RegisterAsync("notif_member8", "notif_member8@test.com");
        Authorize(memberToken);
        (await Client.PostAsJsonAsync($"/api/guilds/join/{guild.InviteCode}", new { }))
            .EnsureSuccessStatusCode();

        var send = await Client.PostAsJsonAsync(
            $"/api/guilds/{guild.Id}/channels/{channel!.Id}/messages",
            new { content = "@everyone anyone there?" }
        );
        // Mentions are a notification side effect, never a send-blocker — the message still sends.
        send.EnsureSuccessStatusCode();

        Authorize(ownerToken);
        await Task.Delay(300);
        var unreadCount = await Client.GetFromJsonAsync<int>("/api/notifications/unread-count");
        unreadCount.Should().Be(0);
    }

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record GuildResponse(long Id, string Name, string? InviteCode);

    private record IdResponse(long Id);

    private record SendMessageResponse(long MessageId);

    private record NotificationDto(
        long Id,
        string Type,
        long ActorId,
        long? GuildId,
        long? ChannelId,
        long? MessageId,
        bool IsRead,
        long CreatedAt
    );
}
