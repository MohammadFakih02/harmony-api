using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Messages;

/// <summary>
/// message-pinning end-to-end (flow #16): a guild pin/unpin round-trip through the real
/// Scylla + hub pipeline (live MessagePinned/MessageUnpinned broadcast + the "pin" system
/// notice appearing in channel history), the PinMessages gating (a plain member is refused),
/// and a DM pin round-trip (any participant may pin).
/// </summary>
public class PinTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public PinTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private HubConnection BuildConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, $"hubs/chat?access_token={accessToken}"),
                options => options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler()
            )
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString
            )
            .Build();

    private void Authorize(string token) =>
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
        Authorize(token);
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "Pin Guild" });
        resp.EnsureSuccessStatusCode();
        var guild = await resp.Content.ReadFromJsonAsync<IdDto>();
        return (guild!.Id, await CreateInviteCodeAsync(guild.Id));
    }

    private async Task<long> CreateChannelAsync(string token, long guildId)
    {
        Authorize(token);
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "general", type = "text", position = 0 }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private async Task<long> SendGuildMessageAsync(long guildId, long channelId, string content)
    {
        var resp = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages",
            new { content }
        );
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SendDto>())!.MessageId;
    }

    // -------------------------------------------------------------------------
    // Guild pin/unpin round-trip (hub + Scylla)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GuildPin_RoundTrips_BroadcastsAndPostsSystemNotice_ThenUnpins()
    {
        var (token, _) = await RegisterAsync("pin_owner1", "pin_owner1@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);

        var pinned = new List<PinBroadcast>();
        var unpinned = new List<PinBroadcast>();
        var systemNotices = new List<MsgReceived>();
        var connection = BuildConnection(token);
        connection.On<PinBroadcast>("MessagePinned", p => pinned.Add(p));
        connection.On<PinBroadcast>("MessageUnpinned", p => unpinned.Add(p));
        connection.On<MsgReceived>("MessageReceived", m => systemNotices.Add(m));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", channelId);

        try
        {
            Authorize(token);
            var messageId = await SendGuildMessageAsync(guildId, channelId, "pin me");

            // Wait for the message to persist before pinning it.
            await Eventually.GetAsync(
                action: () => Task.FromResult(systemNotices),
                predicate: ms => ms.Any(m => m.MessageId == messageId),
                retries: 100,
                intervalMs: 100
            );

            // Pin → 204.
            var pin = await Client.PutAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/pins/{messageId}", null);
            pin.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Live MessagePinned broadcast + the "pin" system notice both arrive.
            await Eventually.GetAsync(
                action: () => Task.FromResult(pinned),
                predicate: ps => ps.Any(p => p.MessageId == messageId && p.ChannelId == channelId),
                retries: 100,
                intervalMs: 100
            );
            await Eventually.GetAsync(
                action: () => Task.FromResult(systemNotices),
                predicate: ms => ms.Any(m => m.MessageType == "pin"),
                retries: 100,
                intervalMs: 100
            );

            // GET pins lists the message.
            var pins = await Client.GetFromJsonAsync<List<PinDto>>(
                $"/api/guilds/{guildId}/channels/{channelId}/pins");
            pins.Should().ContainSingle(p => p.Message.MessageId == messageId
                && p.Message.Content == "pin me");

            // Unpin → 204, MessageUnpinned broadcast, empty list.
            var unpin = await Client.DeleteAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/pins/{messageId}");
            unpin.StatusCode.Should().Be(HttpStatusCode.NoContent);

            await Eventually.GetAsync(
                action: () => Task.FromResult(unpinned),
                predicate: us => us.Any(u => u.MessageId == messageId),
                retries: 100,
                intervalMs: 100
            );

            var after = await Client.GetFromJsonAsync<List<PinDto>>(
                $"/api/guilds/{guildId}/channels/{channelId}/pins");
            after.Should().BeEmpty();
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task GuildPin_ByMemberWithoutPinMessages_Returns403()
    {
        var (ownerToken, _) = await RegisterAsync("pin_owner2", "pin_owner2@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        // Owner sends a message (owner resolves to all bits).
        Authorize(ownerToken);
        var messageId = await SendGuildMessageAsync(guildId, channelId, "some message");

        // A plain member joins — @everyone lacks PinMessages, so they cannot pin.
        var (memberToken, _) = await RegisterAsync("pin_member2", "pin_member2@test.com");
        Authorize(memberToken);
        (await Client.PostAsJsonAsync($"/api/invites/{invite}/join", new { })).EnsureSuccessStatusCode();

        var pin = await Client.PutAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/pins/{messageId}", null);
        pin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -------------------------------------------------------------------------
    // DM pin round-trip (any participant)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DmPin_RoundTrips_ForAParticipant()
    {
        var (tokenA, _) = await RegisterAsync("pin_dm_a", "pin_dm_a@test.com");
        var (tokenB, idB) = await RegisterAsync("pin_dm_b", "pin_dm_b@test.com");

        Authorize(tokenA);
        var dm = await (await Client.PostAsJsonAsync("/api/dm", new { targetUserId = idB }))
            .Content.ReadFromJsonAsync<DmDto>();

        var send = await Client.PostAsJsonAsync($"/api/dm/{dm!.ChannelId}/messages", new { content = "dm pin" });
        var messageId = (await send.Content.ReadFromJsonAsync<SendDto>())!.MessageId;

        // Wait for persistence, then pin.
        await Eventually.GetAsync(
            action: async () =>
            {
                var resp = await Client.GetAsync($"/api/dm/{dm.ChannelId}/messages");
                if (!resp.IsSuccessStatusCode) return new List<MsgReceived>();
                var body = await resp.Content.ReadFromJsonAsync<MessagesDto>();
                return body?.Messages ?? new List<MsgReceived>();
            },
            predicate: ms => ms.Any(m => m.MessageId == messageId),
            retries: 100,
            intervalMs: 100
        );

        var pin = await Client.PutAsync($"/api/dm/{dm.ChannelId}/pins/{messageId}", null);
        pin.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The peer (also a participant) sees the pin.
        Authorize(tokenB);
        var pins = await Client.GetFromJsonAsync<List<PinDto>>($"/api/dm/{dm.ChannelId}/pins");
        pins.Should().ContainSingle(p => p.Message.MessageId == messageId && p.Message.Content == "dm pin");
    }

    // -------------------------------------------------------------------------
    // DTOs
    // -------------------------------------------------------------------------

    private record AuthResponse(string AccessToken, UserDto User);
    private record UserDto(long Id);
    private record IdDto(long Id);
    private record SendDto(long MessageId, long ChannelId, long? GuildId, string Content);
    private record PinBroadcast(long MessageId, long ChannelId);
    private record MsgReceived(long MessageId, string Content, string MessageType);
    private record MessagesDto(List<MsgReceived> Messages, bool Degraded);
    private record PinDto(MsgReceived Message, long PinnedBy, long PinnedAt);
    private record DmDto(long ChannelId, bool IsGroup, List<DmParticipantDto> Participants);
    private record DmParticipantDto(long UserId, string Username, string? AvatarKey);
}
