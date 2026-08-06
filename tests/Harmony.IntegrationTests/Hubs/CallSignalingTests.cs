using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harmony.Domain.Domain.Entities;
using Harmony.Infrastructure.Postgres;
using Harmony.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harmony.IntegrationTests.Hubs;

/// <summary>
/// DM/group-DM call ringing (LiveKit Slice 4): StartCall/CancelCall/DeclineCall over a real
/// two-connection SignalR session, against real Redis ring state and the real message pipeline
/// (the missed-call system message rides RabbitMQ → Scylla → broadcast). The push dispatcher is a
/// !isTest-gated hosted service, so the "call" outbox row StartCall stages stays observable.
/// </summary>
public class CallSignalingTests : ApiTestBase, IClassFixture<HarmonyWebApplicationFactory>
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    public CallSignalingTests(HarmonyWebApplicationFactory factory)
        : base(factory) { }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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

    private async Task<long> OpenDmAsync(string token, long targetUserId)
    {
        Authorize(token);
        var resp = await Client.PostAsJsonAsync("/api/dm", new { targetUserId });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<DmDto>())!.ChannelId;
    }

    private HubConnection BuildConnection(string accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(Factory.Server.BaseAddress, $"hubs/chat?access_token={accessToken}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                }
            )
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString
            )
            .Build();
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> tcs, string what)
    {
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(EventTimeout));
        winner.Should().Be(tcs.Task, $"expected {what} within {EventTimeout.TotalSeconds}s");
        return await tcs.Task;
    }

    // -------------------------------------------------------------------------
    // StartCall
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartCall_RingsTheOtherParticipant_NotTheCaller()
    {
        var (alice, aliceId) = await RegisterAsync("call_a1", "call_a1@test.com");
        var (bob, bobId) = await RegisterAsync("call_b1", "call_b1@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await using var callee = BuildConnection(bob);

        var bobRing = new TaskCompletionSource<IncomingCallDto>();
        callee.On<IncomingCallDto>("IncomingCall", p => bobRing.TrySetResult(p));
        var aliceRang = false;
        caller.On<IncomingCallDto>("IncomingCall", _ => aliceRang = true);

        await caller.StartAsync();
        await callee.StartAsync();

        await caller.InvokeAsync("JoinVoice", channelId);
        await caller.InvokeAsync("StartCall", channelId);

        var ring = await WaitAsync(bobRing, "the callee's IncomingCall");
        ring.ChannelId.Should().Be(channelId);
        ring.CallerId.Should().Be(aliceId);
        ring.StartedAt.Should().BeGreaterThan(0);
        aliceRang.Should().BeFalse("the ring targets participants minus the caller");
    }

    [Fact]
    public async Task StartCall_GuildChannel_Throws()
    {
        var (alice, _) = await RegisterAsync("call_a2", "call_a2@test.com");
        Authorize(alice);
        var g = await Client.PostAsJsonAsync("/api/guilds", new { name = "Call Guild" });
        g.EnsureSuccessStatusCode();
        var guildId = (await g.Content.ReadFromJsonAsync<IdDto>())!.Id;
        var c = await Client.PostAsJsonAsync(
            $"/api/guilds/{guildId}/channels",
            new { name = "vc", type = "voice" }
        );
        c.EnsureSuccessStatusCode();
        var channelId = (await c.Content.ReadFromJsonAsync<IdDto>())!.Id;

        await using var caller = BuildConnection(alice);
        await caller.StartAsync();
        await caller.InvokeAsync("JoinVoice", channelId);

        var act = async () => await caller.InvokeAsync("StartCall", channelId);
        await act.Should().ThrowAsync<HubException>().WithMessage("*direct-message*");
    }

    [Fact]
    public async Task StartCall_NonParticipant_Throws()
    {
        var (alice, _) = await RegisterAsync("call_a3", "call_a3@test.com");
        var (_, bobId) = await RegisterAsync("call_b3", "call_b3@test.com");
        var (carol, _) = await RegisterAsync("call_c3", "call_c3@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var outsider = BuildConnection(carol);
        await outsider.StartAsync();

        var act = async () => await outsider.InvokeAsync("StartCall", channelId);
        await act.Should().ThrowAsync<HubException>().WithMessage("*not a participant*");
    }

    [Fact]
    public async Task StartCall_WithoutJoiningTheRoomFirst_Throws()
    {
        var (alice, _) = await RegisterAsync("call_a4", "call_a4@test.com");
        var (_, bobId) = await RegisterAsync("call_b4", "call_b4@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await caller.StartAsync();

        var act = async () => await caller.InvokeAsync("StartCall", channelId);
        await act.Should().ThrowAsync<HubException>().WithMessage("*Join the call*");
    }

    [Fact]
    public async Task StartCall_WhenAnotherParticipantIsAlreadyInTheRoom_Throws()
    {
        var (alice, _) = await RegisterAsync("call_a5", "call_a5@test.com");
        var (bob, bobId) = await RegisterAsync("call_b5", "call_b5@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await using var callee = BuildConnection(bob);
        await caller.StartAsync();
        await callee.StartAsync();

        await callee.InvokeAsync("JoinVoice", channelId); // an ongoing call is joined, not re-rung
        await caller.InvokeAsync("JoinVoice", channelId);

        var act = async () => await caller.InvokeAsync("StartCall", channelId);
        await act.Should().ThrowAsync<HubException>().WithMessage("*already in progress*");
    }

    [Fact]
    public async Task DuplicateStartCall_RingsOnlyOnce()
    {
        var (alice, _) = await RegisterAsync("call_a6", "call_a6@test.com");
        var (bob, bobId) = await RegisterAsync("call_b6", "call_b6@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await using var callee = BuildConnection(bob);

        var rings = 0;
        var firstRing = new TaskCompletionSource<IncomingCallDto>();
        callee.On<IncomingCallDto>(
            "IncomingCall",
            p =>
            {
                Interlocked.Increment(ref rings);
                firstRing.TrySetResult(p);
            }
        );

        await caller.StartAsync();
        await callee.StartAsync();

        await caller.InvokeAsync("JoinVoice", channelId);
        await caller.InvokeAsync("StartCall", channelId);
        await caller.InvokeAsync("StartCall", channelId); // NX-rejected → silent no-op

        await WaitAsync(firstRing, "the first ring");
        await Task.Delay(1000); // grace for a (wrong) second delivery
        rings.Should().Be(1);
    }

    [Fact]
    public async Task StartCall_StagesACallPushOutboxRow()
    {
        var (alice, aliceId) = await RegisterAsync("call_a7", "call_a7@test.com");
        var (_, bobId) = await RegisterAsync("call_b7", "call_b7@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await caller.StartAsync();
        await caller.InvokeAsync("JoinVoice", channelId);
        await caller.InvokeAsync("StartCall", channelId);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();
        var row = await db
            .PushOutbox.AsNoTracking()
            .SingleAsync(m => m.Kind == "call" && m.ChannelId == channelId);
        row.ActorId.Should().Be(aliceId);
        row.RecipientId.Should().Be(0, "the dispatcher fans out to participants minus the actor");
        row.GuildId.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // CancelCall
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CancelCall_Missed_DismissesTheCallee_AndPostsAMissedCallMessage()
    {
        var (alice, _) = await RegisterAsync("call_a8", "call_a8@test.com");
        var (bob, bobId) = await RegisterAsync("call_b8", "call_b8@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await using var callee = BuildConnection(bob);

        var ring = new TaskCompletionSource<IncomingCallDto>();
        var cancelled = new TaskCompletionSource<CallCancelledDto>();
        var missedMessage = new TaskCompletionSource<MessageDto>();
        callee.On<IncomingCallDto>("IncomingCall", p => ring.TrySetResult(p));
        callee.On<CallCancelledDto>("CallCancelled", p => cancelled.TrySetResult(p));
        callee.On<MessageDto>(
            "MessageReceived",
            m =>
            {
                if (m.MessageType == "missed_call")
                    missedMessage.TrySetResult(m);
            }
        );

        await caller.StartAsync();
        await callee.StartAsync();
        await callee.InvokeAsync("JoinChannel", channelId); // channel group carries MessageReceived

        await caller.InvokeAsync("JoinVoice", channelId);
        await caller.InvokeAsync("StartCall", channelId);
        await WaitAsync(ring, "the callee's IncomingCall");

        await caller.InvokeAsync("LeaveVoice", channelId);
        await caller.InvokeAsync("CancelCall", channelId, true);

        (await WaitAsync(cancelled, "the callee's CallCancelled")).ChannelId.Should().Be(channelId);
        var missed = await WaitAsync(missedMessage, "the missed_call system message");
        missed.ChannelId.Should().Be(channelId);
    }

    [Fact]
    public async Task CancelCall_AfterTheCalleeAnswered_PostsNoMissedCall()
    {
        var (alice, _) = await RegisterAsync("call_a9", "call_a9@test.com");
        var (bob, bobId) = await RegisterAsync("call_b9", "call_b9@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await using var callee = BuildConnection(bob);

        var ring = new TaskCompletionSource<IncomingCallDto>();
        callee.On<IncomingCallDto>("IncomingCall", p => ring.TrySetResult(p));

        await caller.StartAsync();
        await callee.StartAsync();

        await caller.InvokeAsync("JoinVoice", channelId);
        await caller.InvokeAsync("StartCall", channelId);
        await WaitAsync(ring, "the callee's IncomingCall");

        await callee.InvokeAsync("JoinVoice", channelId); // answering ends the ring key

        // A caller-timeout racing the accept must not produce a missed-call notice.
        await caller.InvokeAsync("CancelCall", channelId, true);
        await Task.Delay(2000); // grace for a (wrong) message to traverse the pipeline

        Authorize(alice);
        var history = await Client.GetFromJsonAsync<MessagesDto>(
            $"/api/dm/{channelId}/messages?limit=20"
        );
        history!.Messages.Should().NotContain(m => m.MessageType == "missed_call");
    }

    [Fact]
    public async Task CancelCall_ByANonCaller_DoesNothing()
    {
        var (alice, _) = await RegisterAsync("call_a10", "call_a10@test.com");
        var (bob, bobId) = await RegisterAsync("call_b10", "call_b10@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await using var callee = BuildConnection(bob);

        var ring = new TaskCompletionSource<IncomingCallDto>();
        var cancelled = new TaskCompletionSource<CallCancelledDto>();
        callee.On<IncomingCallDto>("IncomingCall", p => ring.TrySetResult(p));
        callee.On<CallCancelledDto>("CallCancelled", p => cancelled.TrySetResult(p));

        await caller.StartAsync();
        await callee.StartAsync();

        await caller.InvokeAsync("JoinVoice", channelId);
        await caller.InvokeAsync("StartCall", channelId);
        await WaitAsync(ring, "the callee's IncomingCall");

        // Bob (the callee) trying to cancel Alice's ring is a silent no-op...
        await callee.InvokeAsync("CancelCall", channelId, true);
        cancelled.Task.IsCompleted.Should().BeFalse();

        // ...the ring is still Alice's to end — her cancel still reaches Bob.
        await caller.InvokeAsync("CancelCall", channelId, false);
        (await WaitAsync(cancelled, "Alice's CallCancelled")).ChannelId.Should().Be(channelId);
    }

    // -------------------------------------------------------------------------
    // DeclineCall
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeclineCall_NotifiesTheCaller_AndDismissesTheDeclinersTabs()
    {
        var (alice, _) = await RegisterAsync("call_a11", "call_a11@test.com");
        var (bob, bobId) = await RegisterAsync("call_b11", "call_b11@test.com");
        var channelId = await OpenDmAsync(alice, bobId);

        await using var caller = BuildConnection(alice);
        await using var callee = BuildConnection(bob);

        var ring = new TaskCompletionSource<IncomingCallDto>();
        var declined = new TaskCompletionSource<CallDeclinedDto>();
        var dismissed = new TaskCompletionSource<CallCancelledDto>();
        callee.On<IncomingCallDto>("IncomingCall", p => ring.TrySetResult(p));
        caller.On<CallDeclinedDto>("CallDeclined", p => declined.TrySetResult(p));
        callee.On<CallCancelledDto>("CallCancelled", p => dismissed.TrySetResult(p));

        await caller.StartAsync();
        await callee.StartAsync();

        await caller.InvokeAsync("JoinVoice", channelId);
        await caller.InvokeAsync("StartCall", channelId);
        await WaitAsync(ring, "the callee's IncomingCall");

        await callee.InvokeAsync("DeclineCall", channelId);

        var decline = await WaitAsync(declined, "the caller's CallDeclined");
        decline.ChannelId.Should().Be(channelId);
        decline.UserId.Should().Be(bobId);
        (await WaitAsync(dismissed, "the decliner's own CallCancelled"))
            .ChannelId.Should()
            .Be(channelId);
    }

    // -------------------------------------------------------------------------
    // DTOs local to this test file (server serializes Snowflake longs as strings;
    // the connection's AllowReadingFromString maps them back)
    // -------------------------------------------------------------------------

    private record IncomingCallDto(long ChannelId, long CallerId, long StartedAt);

    private record CallCancelledDto(long ChannelId);

    private record CallDeclinedDto(long ChannelId, long UserId);

    private record MessageDto(long ChannelId, string MessageType);

    private record MessagesDto(List<MessageDto> Messages);

    private record DmDto(long ChannelId);

    private record AuthResponse(string AccessToken, UserDto User);

    private record UserDto(long Id);

    private record IdDto(long Id);
}
