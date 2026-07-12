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
/// emoji-reactions end-to-end (slice 1): a guild react/unreact round-trip through the real hub
/// (live ReactionAdded/ReactionRemoved broadcast) with the aggregated summary appearing on the
/// message load (count + meReacted), and the DM path (any participant may react; a non-participant
/// is refused).
/// </summary>
public class ReactionTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    public ReactionTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

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
        var resp = await Client.PostAsJsonAsync("/api/guilds", new { name = "React Guild" });
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

    private async Task<List<MsgReceived>> GuildMessagesAsync(long guildId, long channelId)
    {
        var resp = await Client.GetAsync($"/api/guilds/{guildId}/channels/{channelId}/messages");
        if (!resp.IsSuccessStatusCode)
            return new List<MsgReceived>();
        var body = await resp.Content.ReadFromJsonAsync<MessagesDto>();
        return body?.Messages ?? new List<MsgReceived>();
    }

    // -------------------------------------------------------------------------
    // Guild react/unreact round-trip (hub + summary on load)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GuildReaction_RoundTrips_BroadcastsAndSummarizes_ThenRemoves()
    {
        const string emoji = "😀";
        var (token, userId) = await RegisterAsync("react_owner1", "react_owner1@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);

        var added = new List<ReactionBroadcast>();
        var removed = new List<ReactionBroadcast>();
        var received = new List<MsgReceived>();
        var connection = BuildConnection(token);
        connection.On<ReactionBroadcast>("ReactionAdded", r => added.Add(r));
        connection.On<ReactionBroadcast>("ReactionRemoved", r => removed.Add(r));
        connection.On<MsgReceived>("MessageReceived", m => received.Add(m));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", channelId);

        try
        {
            Authorize(token);
            var messageId = await SendGuildMessageAsync(guildId, channelId, "react to me");

            // Wait for the message to persist before reacting.
            await Eventually.GetAsync(
                action: () => Task.FromResult(received),
                predicate: ms => ms.Any(m => m.MessageId == messageId),
                retries: 100,
                intervalMs: 100
            );

            // React → 204.
            var react = await Client.PutAsJsonAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions",
                new { emoji }
            );
            react.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Live ReactionAdded broadcast arrives with the actor + emoji.
            await Eventually.GetAsync(
                action: () => Task.FromResult(added),
                predicate: rs => rs.Any(r =>
                    r.MessageId == messageId && r.Emoji == emoji && r.UserId == userId),
                retries: 100,
                intervalMs: 100
            );

            // GET messages carries the aggregated pill (count 1, meReacted true).
            var messages = await GuildMessagesAsync(guildId, channelId);
            var msg = messages.Single(m => m.MessageId == messageId);
            msg.Reactions.Should().ContainSingle(x =>
                x.Emoji == emoji && x.Count == 1 && x.MeReacted);

            // Re-reacting the same emoji is idempotent — still count 1.
            (await Client.PutAsJsonAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions",
                new { emoji })).StatusCode.Should().Be(HttpStatusCode.NoContent);
            var afterDup = (await GuildMessagesAsync(guildId, channelId))
                .Single(m => m.MessageId == messageId);
            afterDup.Reactions.Single(x => x.Emoji == emoji).Count.Should().Be(1);

            // Unreact → 204 + ReactionRemoved broadcast + summary drops the emoji.
            var unreact = await Client.DeleteAsync(
                $"/api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions?emoji={Uri.EscapeDataString(emoji)}");
            unreact.StatusCode.Should().Be(HttpStatusCode.NoContent);

            await Eventually.GetAsync(
                action: () => Task.FromResult(removed),
                predicate: rs => rs.Any(r => r.MessageId == messageId && r.Emoji == emoji),
                retries: 100,
                intervalMs: 100
            );

            var afterRemove = (await GuildMessagesAsync(guildId, channelId))
                .Single(m => m.MessageId == messageId);
            afterRemove.Reactions.Should().NotContain(x => x.Emoji == emoji);
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task GuildReaction_AggregatesTwoUsers_AndMeReactedIsPerViewer()
    {
        const string emoji = "🔥";
        var (ownerToken, ownerId) = await RegisterAsync("react_owner2", "react_owner2@test.com");
        var (guildId, invite) = await CreateGuildAsync(ownerToken);
        var channelId = await CreateChannelAsync(ownerToken, guildId);

        Authorize(ownerToken);
        var messageId = await SendGuildMessageAsync(guildId, channelId, "count me");
        // Wait for persistence.
        await Eventually.GetAsync(
            action: () => GuildMessagesAsync(guildId, channelId),
            predicate: ms => ms.Any(m => m.MessageId == messageId),
            retries: 100,
            intervalMs: 100
        );

        // A second member joins and both react with the same emoji.
        var (memberToken, memberId) = await RegisterAsync("react_member2", "react_member2@test.com");
        Authorize(memberToken);
        (await Client.PostAsJsonAsync($"/api/invites/{invite}/join", new { })).EnsureSuccessStatusCode();

        Authorize(ownerToken);
        (await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions",
            new { emoji })).EnsureSuccessStatusCode();
        Authorize(memberToken);
        (await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions",
            new { emoji })).EnsureSuccessStatusCode();

        // The member sees count 2, meReacted true (they are one of the two).
        var asMember = (await GuildMessagesAsync(guildId, channelId))
            .Single(m => m.MessageId == messageId);
        asMember.Reactions.Single(x => x.Emoji == emoji).Count.Should().Be(2);
        asMember.Reactions.Single(x => x.Emoji == emoji).MeReacted.Should().BeTrue();

        // The owner also sees count 2, meReacted true.
        Authorize(ownerToken);
        var asOwner = (await GuildMessagesAsync(guildId, channelId))
            .Single(m => m.MessageId == messageId);
        asOwner.Reactions.Single(x => x.Emoji == emoji).MeReacted.Should().BeTrue();

        _ = (ownerId, memberId);
    }

    // -------------------------------------------------------------------------
    // DM react (any participant; non-participant refused)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DmReaction_RoundTrips_ForAParticipant_AndRefusesOutsiders()
    {
        const string emoji = "👍";
        var (tokenA, _) = await RegisterAsync("react_dm_a", "react_dm_a@test.com");
        var (tokenB, idB) = await RegisterAsync("react_dm_b", "react_dm_b@test.com");
        var (tokenC, _) = await RegisterAsync("react_dm_c", "react_dm_c@test.com");

        Authorize(tokenA);
        var dm = await (await Client.PostAsJsonAsync("/api/dm", new { targetUserId = idB }))
            .Content.ReadFromJsonAsync<DmDto>();

        var send = await Client.PostAsJsonAsync(
            $"/api/dm/{dm!.ChannelId}/messages", new { content = "dm react" });
        var messageId = (await send.Content.ReadFromJsonAsync<SendDto>())!.MessageId;

        // Wait for persistence.
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

        // Participant B reacts → 204.
        Authorize(tokenB);
        var react = await Client.PutAsJsonAsync(
            $"/api/dm/{dm.ChannelId}/messages/{messageId}/reactions", new { emoji });
        react.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A sees the reaction on load.
        Authorize(tokenA);
        var msg = (await (await Client.GetAsync($"/api/dm/{dm.ChannelId}/messages"))
            .Content.ReadFromJsonAsync<MessagesDto>())!.Messages.Single(m => m.MessageId == messageId);
        msg.Reactions.Should().ContainSingle(x => x.Emoji == emoji && x.Count == 1);

        // Outsider C (not a participant) is refused.
        Authorize(tokenC);
        var refused = await Client.PutAsJsonAsync(
            $"/api/dm/{dm.ChannelId}/messages/{messageId}/reactions", new { emoji });
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GuildReaction_WithCustomPrefix_IsRejected()
    {
        var (token, _) = await RegisterAsync("react_owner3", "react_owner3@test.com");
        var (guildId, _) = await CreateGuildAsync(token);
        var channelId = await CreateChannelAsync(token, guildId);

        Authorize(token);
        var messageId = await SendGuildMessageAsync(guildId, channelId, "no custom yet");
        await Eventually.GetAsync(
            action: () => GuildMessagesAsync(guildId, channelId),
            predicate: ms => ms.Any(m => m.MessageId == messageId),
            retries: 100,
            intervalMs: 100
        );

        // "custom:{id}" is reserved for slice 3 — rejected as a 400.
        var resp = await Client.PutAsJsonAsync(
            $"/api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions",
            new { emoji = "custom:12345" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -------------------------------------------------------------------------
    // DTOs
    // -------------------------------------------------------------------------

    private record AuthResponse(string AccessToken, UserDto User);
    private record UserDto(long Id);
    private record IdDto(long Id);
    private record SendDto(long MessageId, long ChannelId, long? GuildId, string Content);
    private record ReactionBroadcast(long MessageId, long ChannelId, long? GuildId, string Emoji, long UserId);
    private record ReactionSummaryDto(string Emoji, int Count, bool MeReacted);
    private record MsgReceived(long MessageId, string Content, List<ReactionSummaryDto> Reactions);
    private record MessagesDto(List<MsgReceived> Messages, bool Degraded);
    private record DmDto(long ChannelId, bool IsGroup, List<DmParticipantDto> Participants);
    private record DmParticipantDto(long UserId, string Username, string? AvatarKey);
}
