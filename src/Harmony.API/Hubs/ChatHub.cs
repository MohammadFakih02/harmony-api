using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Harmony.API.Hubs;

/// <summary>
/// Strongly-typed SignalR hub for real-time chat.
///
/// Architectural rules:
///   1. No direct ScyllaDB or RabbitMQ access — all writes go through IMessageService.
///   2. No broadcasting from hub methods — ScyllaMessageConsumer broadcasts via
///      IHubBroadcaster after ScyllaDB persistence is confirmed.
///   3. JWT is passed via query-string (?access_token=...) for WebSocket connections.
/// </summary>
[Authorize]
public class ChatHub : Hub<IChatClient>
{
    private readonly IMessageService _messageService;
    private readonly IGuildRepository _guildRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IPermissionService _permissions;
    private readonly IPresenceService _presence;
    private readonly IVoiceStateService _voice;
    private readonly ILiveKitRoomService _liveKitRooms;
    private readonly IDirectMessageRepository _dms;
    private readonly IHubBroadcaster _broadcaster;
    private readonly IPushOutboxRepository _pushOutbox;
    private readonly IPushDispatchNudge _pushNudge;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IMessageService messageService,
        IGuildRepository guildRepository,
        IChannelRepository channelRepository,
        IPermissionService permissions,
        IPresenceService presence,
        IVoiceStateService voice,
        ILiveKitRoomService liveKitRooms,
        IDirectMessageRepository dms,
        IHubBroadcaster broadcaster,
        IPushOutboxRepository pushOutbox,
        IPushDispatchNudge pushNudge,
        ISnowflakeIdGenerator snowflake,
        ILogger<ChatHub> logger
    )
    {
        _messageService = messageService;
        _guildRepository = guildRepository;
        _channelRepository = channelRepository;
        _permissions = permissions;
        _presence = presence;
        _voice = voice;
        _liveKitRooms = liveKitRooms;
        _dms = dms;
        _broadcaster = broadcaster;
        _pushOutbox = pushOutbox;
        _pushNudge = pushNudge;
        _snowflake = snowflake;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------------------------

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation(
            "ChatHub: user {UserId} connected — ConnectionId: {ConnectionId}",
            GetUserId(),
            Context.ConnectionId
        );
        await _presence.SetOnlineAsync(GetUserId(), Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        await _presence.SetOfflineAsync(userId, Context.ConnectionId);

        // Drop voice state only once the user's LAST connection is gone — a second tab/device may
        // still be in the call. IsConnectedAsync fails closed (true when uncertain), so we skip the
        // leave rather than risk yanking a live participant; the VoiceStateSweepService backstops
        // a genuine crash where no connection remains.
        if (!await _presence.IsConnectedAsync(userId))
            await _voice.LeaveAsync(userId);

        if (exception is not null)
            _logger.LogWarning(
                exception,
                "ChatHub: user {UserId} disconnected with error — ConnectionId: {ConnectionId}",
                GetUserId(),
                Context.ConnectionId
            );
        else
            _logger.LogInformation(
                "ChatHub: user {UserId} disconnected cleanly — ConnectionId: {ConnectionId}",
                GetUserId(),
                Context.ConnectionId
            );

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Periodic client keep-alive (every 45s per the client contract). Refreshes
    /// the user's presence TTL without broadcasting — connect/disconnect own that.
    /// </summary>
    public async Task Heartbeat()
    {
        await _presence.HeartbeatAsync(GetUserId(), Context.ConnectionId);
    }

    /// <summary>
    /// Client-reported activity signal. The client calls SetIdle(true) after ~15 min
    /// with no user interaction, and SetIdle(false) on the next interaction. Only shifts
    /// the effective status when the user's preferred status is "online" (online ↔ away).
    /// </summary>
    public async Task SetIdle(bool idle)
    {
        await _presence.SetIdleAsync(GetUserId(), idle);
    }

    // -------------------------------------------------------------------------
    // Group management
    // -------------------------------------------------------------------------

    public async Task JoinChannel(long channelId)
    {
        var userId = GetUserId();

        // Authorize: verify the caller can view the channel before subscribing to its broadcast
        // group. Without this, any authenticated client could join channel:{id} and receive its
        // messages.
        if (!await CanAccessChannelAsync(userId, channelId))
            throw new HubException("You do not have permission to view this channel.");

        await Groups.AddToGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
        _logger.LogDebug("User {UserId} joined {Group}", userId, ChannelGroup(channelId));
    }

    /// <summary>
    /// Whether a user may access a channel: for a guild channel, ViewChannel (overrides applied;
    /// non-members resolve to 0); for a guild-less DM, the caller must be a participant. A missing
    /// channel is not accessible. Shared by JoinChannel and StartTyping.
    /// </summary>
    private async Task<bool> CanAccessChannelAsync(long userId, long channelId)
    {
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel is null)
            return false;

        return channel.GuildId is { } guildId
            ? await _permissions.HasAsync(userId, guildId, Permission.ViewChannel, channelId)
            : await _dms.IsParticipantAsync(channelId, userId);
    }

    public async Task LeaveChannel(long channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
        _logger.LogDebug("User {UserId} left {Group}", GetUserId(), ChannelGroup(channelId));
    }

    public async Task JoinGuild(long guildId)
    {
        var userId = GetUserId();

        // Authorize: only members may subscribe to a guild's broadcast group.
        if (!await _guildRepository.IsMemberAsync(guildId, userId))
            throw new HubException("You are not a member of this guild.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GuildGroup(guildId));
        _logger.LogDebug("User {UserId} joined {Group}", userId, GuildGroup(guildId));
    }

    public async Task LeaveGuild(long guildId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GuildGroup(guildId));
        _logger.LogDebug("User {UserId} left {Group}", GetUserId(), GuildGroup(guildId));
    }

    // -------------------------------------------------------------------------
    // Client → server actions (Exception-free Result Pattern)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a message through the full async pipeline.
    /// Returns a HubResult envelope indicating success or failure.
    /// </summary>
    public async Task<HubResult<SendMessageResponse>> SendMessage(
        long channelId,
        long guildId,
        string content
    )
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
        {
            return new HubResult<SendMessageResponse>(
                Succeeded: false,
                Data: null,
                ErrorMessage: "Message content must be between 1 and 2000 characters."
            );
        }

        var userId = GetUserId();

        try
        {
            var response = await _messageService.SendMessageAsync(
                userId,
                guildId,
                channelId,
                new SendMessageRequest(
                    Content: content,
                    ReplyToId: null,
                    AttachmentIds: null
                )
            );

            _logger.LogDebug(
                "Hub: SendMessage accepted — MessageId: {MessageId}, ChannelId: {ChannelId}",
                response.MessageId,
                channelId
            );

            return new HubResult<SendMessageResponse>(
                Succeeded: true,
                Data: response,
                ErrorMessage: null
            );
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("Hub validation warning: {Message}", ex.Message);
            return new HubResult<SendMessageResponse>(
                Succeeded: false,
                Data: null,
                ErrorMessage: ex.Message
            );
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Hub validation warning: {Message}", ex.Message);
            return new HubResult<SendMessageResponse>(
                Succeeded: false,
                Data: null,
                ErrorMessage: ex.Message
            );
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Hub validation warning: {Message}", ex.Message);
            return new HubResult<SendMessageResponse>(
                Succeeded: false,
                Data: null,
                ErrorMessage: ex.Message
            );
        }
    }

    /// <summary>
    /// Ephemeral typing signal. The client throttles this to at most once every few seconds while
    /// the composer has focus + content. Verifies channel access (so you can't spoof typing into a
    /// channel you can't see), then broadcasts TypingStarted to the channel group (the typer's own
    /// client filters itself). Nothing is persisted.
    /// </summary>
    public async Task StartTyping(long channelId)
    {
        var userId = GetUserId();
        if (!await CanAccessChannelAsync(userId, channelId))
            return; // silently ignore — typing is a best-effort signal, not an action to error on

        await _broadcaster.BroadcastTypingStartedAsync(channelId, userId);
    }

    /// <summary>Clears the caller's typing indicator in a channel (sent on message send).</summary>
    public async Task StopTyping(long channelId)
    {
        await _broadcaster.BroadcastTypingStoppedAsync(channelId, GetUserId());
    }

    // -------------------------------------------------------------------------
    // Voice signaling (ephemeral — rides the hub + broadcaster, like typing/presence)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Joins the caller to a voice channel/DM-call room (evicting any room they were already in).
    /// Authorization mirrors CanAccessChannelAsync but gates on <see cref="Permission.ConnectVoice"/>
    /// for a guild channel (a DM only needs participation). The LiveKit token itself is minted by the
    /// REST endpoint; this method just publishes the participant into the shared Redis voice state.
    /// </summary>
    public async Task JoinVoice(long channelId)
    {
        var userId = GetUserId();
        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel is null)
            throw new HubException("Channel not found.");

        if (!await CanConnectVoiceAsync(userId, channel))
            throw new HubException("You do not have permission to join this voice channel.");

        // UserLimit: reject a join into a full guild channel — unless the joiner holds MoveMembers
        // (Discord's bypass), or is already in the room (a reconnect must never bounce off the cap).
        if (channel.GuildId is { } limitGuildId && channel.UserLimit is int limit && limit > 0)
        {
            var participants = await _voice.GetChannelParticipantsAsync(channelId);
            if (
                participants.Count >= limit
                && participants.All(p => p.UserId != userId)
                && !await _permissions.HasAsync(userId, limitGuildId, Permission.MoveMembers, channelId)
            )
                throw new HubException("This voice channel is full.");
        }

        await _voice.JoinAsync(channelId, channel.GuildId, userId);

        // Best-effort re-arm of a sticky server mute/deafen carried across a rejoin (the soft
        // flags were re-seeded by JoinAsync; this re-applies the LiveKit-side enforcement).
        if (channel.GuildId is not null)
        {
            var self = (await _voice.GetChannelParticipantsAsync(channelId)).FirstOrDefault(p =>
                p.UserId == userId
            );
            if (self is { IsServerMuted: true })
                await _liveKitRooms.SetMicrophoneMutedAsync(channelId, userId, muted: true);
            if (self is { IsServerDeafened: true })
                await _liveKitRooms.SetCanSubscribeAsync(channelId, userId, canSubscribe: false);
        }

        // A callee joining a ringing DM answers the call — end the ring so a later
        // CancelCall (caller timeout racing the accept) can't post a missed-call notice.
        if (
            channel.GuildId is null
            && await _voice.GetRingCallerAsync(channelId) is { } ringCaller
            && ringCaller != userId
        )
            await _voice.TryEndRingAsync(channelId);

        _logger.LogDebug("User {UserId} joined voice {ChannelId}", userId, channelId);
    }

    /// <summary>
    /// Leaves whatever voice room the caller is in. The service is authoritative on the current room,
    /// so no authorization is needed — you can always leave a call you're in; <paramref name="channelId"/>
    /// is advisory (kept for client symmetry / the rate-limit key).
    /// </summary>
    public async Task LeaveVoice(long channelId)
    {
        await _voice.LeaveAsync(GetUserId());
        _logger.LogDebug("User {UserId} left voice {ChannelId}", GetUserId(), channelId);
    }

    /// <summary>
    /// Updates the caller's self-reported voice flags in their current room (mute / deafen / camera /
    /// screenshare). No-op if the caller isn't in a room. Camera/screenshare flags are clamped to the
    /// caller's UseVideo/Stream permissions in guild rooms (DM rooms allow everything) — clamped, not
    /// thrown, so a legitimate mute/deafen riding the same invoke still lands; the LiveKit token's
    /// canPublishSources grant is the hard enforcement, this just keeps the roster honest. The clamped
    /// value broadcasts as usual, so the client's optimistic state self-corrects from the echo.
    /// Moderating <em>other</em> members' state is a deferred follow-up (not in Slice 1).
    /// </summary>
    public async Task UpdateVoiceState(bool isMuted, bool isDeafened, bool isVideoOn, bool isStreaming)
    {
        var userId = GetUserId();

        if (isVideoOn || isStreaming)
        {
            var room = await _voice.GetCurrentRoomAsync(userId);
            if (room is { GuildId: { } guildId } r)
            {
                if (isVideoOn && !await _permissions.HasAsync(userId, guildId, Permission.UseVideo, r.ChannelId))
                    isVideoOn = false;
                if (isStreaming && !await _permissions.HasAsync(userId, guildId, Permission.Stream, r.ChannelId))
                    isStreaming = false;
            }
        }

        await _voice.UpdateStateAsync(userId, isMuted, isDeafened, isVideoOn, isStreaming);
    }

    /// <summary>
    /// Sets/clears a member's server mute and/or server deafen (null = leave that flag alone).
    /// Guild voice rooms only — the target's CURRENT room is resolved server-side (never trusted
    /// from the client), each flag is gated on its own permission bit resolved against that room,
    /// and the flags are sticky per guild member (a leave/rejoin cannot clear them). Soft layer =
    /// Redis + VoiceStateUpdated broadcast; hard layer = LiveKit publish-grant/subscription
    /// enforcement (fail-open — an API hiccup never blocks the moderation itself).
    /// </summary>
    public async Task ModerateVoiceState(long targetUserId, bool? serverMute, bool? serverDeafen)
    {
        if (serverMute is null && serverDeafen is null)
            return;

        var moderatorId = GetUserId();
        var room = await _voice.GetCurrentRoomAsync(targetUserId);
        if (room is not { GuildId: { } guildId } r)
            throw new HubException("That user is not in a voice channel of this server.");

        if (
            serverMute is not null
            && !await _permissions.HasAsync(moderatorId, guildId, Permission.MuteMembers, r.ChannelId)
        )
            throw new HubException("You do not have permission to server mute members.");
        if (
            serverDeafen is not null
            && !await _permissions.HasAsync(moderatorId, guildId, Permission.DeafenMembers, r.ChannelId)
        )
            throw new HubException("You do not have permission to server deafen members.");

        if (!await _voice.ModerateAsync(r.ChannelId, targetUserId, serverMute, serverDeafen))
            return; // target left mid-flight — nothing to moderate anymore

        if (serverMute is { } muted)
            await _liveKitRooms.SetMicrophoneMutedAsync(r.ChannelId, targetUserId, muted);
        if (serverDeafen is { } deafened)
            await _liveKitRooms.SetCanSubscribeAsync(r.ChannelId, targetUserId, !deafened);

        _logger.LogInformation(
            "Voice moderation: user {ModeratorId} set mute={Mute} deafen={Deafen} on {TargetId} in channel {ChannelId}",
            moderatorId,
            serverMute,
            serverDeafen,
            targetUserId,
            r.ChannelId
        );
    }

    /// <summary>
    /// Moves a member to another voice channel of the same guild. Gated on MoveMembers against the
    /// SOURCE channel; the destination must be a voice channel the TARGET could connect to
    /// themselves (a move can't smuggle someone past ConnectVoice). All flags travel with them.
    /// The target's client gets a targeted VoiceForceMoved and reconnects media; a client that
    /// ignores it is removed from the old LiveKit room server-side.
    /// </summary>
    public async Task MoveVoiceParticipant(long targetUserId, long toChannelId)
    {
        var moderatorId = GetUserId();
        var room = await _voice.GetCurrentRoomAsync(targetUserId);
        if (room is not { GuildId: { } guildId } r)
            throw new HubException("That user is not in a voice channel of this server.");
        if (r.ChannelId == toChannelId)
            return;

        var destination = await _channelRepository.GetByIdAsync(toChannelId);
        if (destination is null || destination.GuildId != guildId || destination.Type != "voice")
            throw new HubException("The destination must be a voice channel in the same server.");

        if (!await _permissions.HasAsync(moderatorId, guildId, Permission.MoveMembers, r.ChannelId))
            throw new HubException("You do not have permission to move members.");
        if (!await _permissions.HasAsync(targetUserId, guildId, Permission.ConnectVoice, toChannelId))
            throw new HubException("That user cannot connect to the destination channel.");

        if (!await _voice.MoveAsync(targetUserId, r.ChannelId, toChannelId, guildId))
            return; // target left mid-flight

        await _broadcaster.BroadcastVoiceForceMovedAsync(
            targetUserId,
            new VoiceForceMovedPayload(r.ChannelId, toChannelId, guildId)
        );
        await _liveKitRooms.RemoveParticipantAsync(r.ChannelId, targetUserId);

        _logger.LogInformation(
            "Voice moderation: user {ModeratorId} moved {TargetId} from channel {FromId} to {ToId}",
            moderatorId,
            targetUserId,
            r.ChannelId,
            toChannelId
        );
    }

    /// <summary>
    /// Whether a user may connect to voice in a channel: guild channel → ConnectVoice (overrides
    /// applied); guild-less DM → the caller must be a participant. Mirrors CanAccessChannelAsync but
    /// with the voice permission bit instead of ViewChannel.
    /// </summary>
    private async Task<bool> CanConnectVoiceAsync(long userId, Channel channel) =>
        channel.GuildId is { } guildId
            ? await _permissions.HasAsync(userId, guildId, Permission.ConnectVoice, channel.Id)
            : await _dms.IsParticipantAsync(channel.Id, userId);

    // -------------------------------------------------------------------------
    // DM/group-DM call ringing (Slice 4 — the caller is already in the room via
    // JoinVoice; these methods only manage the ring: who's being alerted and how
    // it ends. Ring state is one fail-open Redis key; see IVoiceStateService.)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rings the other participants of a DM/group-DM the caller has already joined the voice room
    /// of. Rejects guild channels, non-participants, callers not in the room, and rooms that already
    /// have another participant (an ongoing call is joined, not re-rung). Also stages a "call"
    /// push-outbox row so offline participants get a web-push ring — best-effort, like all push.
    /// </summary>
    public async Task StartCall(long channelId)
    {
        var userId = GetUserId();

        var channel = await _channelRepository.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId is not null)
            throw new HubException("Calls can only be started in a direct-message channel.");

        if (!await _dms.IsParticipantAsync(channelId, userId))
            throw new HubException("You are not a participant of this conversation.");

        if ((await _voice.GetCurrentRoomAsync(userId))?.ChannelId != channelId)
            throw new HubException("Join the call's voice room before ringing.");

        if ((await _voice.GetChannelParticipantsAsync(channelId)).Any(p => p.UserId != userId))
            throw new HubException("A call is already in progress.");

        // NX: a live ring for this channel makes this a duplicate — silently ignore.
        if (!await _voice.TryBeginRingAsync(channelId, userId))
            return;

        var recipients = (await _dms.GetParticipantIdsAsync(channelId))
            .Where(id => id != userId)
            .ToList();
        if (recipients.Count == 0)
            return;

        await _broadcaster.BroadcastIncomingCallAsync(
            recipients,
            new IncomingCallPayload(
                channelId,
                userId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            )
        );

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _pushOutbox.AddAsync(
                new PushOutboxMessage
                {
                    Id = _snowflake.NextId(),
                    Kind = PushKind.Call,
                    RecipientId = 0,
                    ActorId = userId,
                    ChannelId = channelId,
                    NextAttemptAt = now,
                    CreatedAt = now,
                }
            );
            await _pushOutbox.SaveChangesAsync();
            _pushNudge.Signal();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "StartCall: push-outbox staging failed for channel {ChannelId} — ring sent, continuing",
                channelId
            );
        }

        _logger.LogDebug("User {UserId} started ringing channel {ChannelId}", userId, channelId);
    }

    /// <summary>
    /// Ends the caller's own ring (manual hang-up while ringing, or the client's 60s timeout).
    /// Only the user who started the live ring may cancel — anyone else silently no-ops (typing-signal
    /// posture; also what makes the missed-call notice unspoofable). When <paramref name="missed"/>
    /// and the ring was still live with nobody else in the room, posts a "missed_call" system message
    /// through the normal message pipeline.
    /// </summary>
    public async Task CancelCall(long channelId, bool missed)
    {
        var userId = GetUserId();

        if (await _voice.GetRingCallerAsync(channelId) != userId)
            return; // no live ring, or not yours to cancel

        var wasLive = await _voice.TryEndRingAsync(channelId);

        var recipients = (await _dms.GetParticipantIdsAsync(channelId))
            .Where(id => id != userId)
            .ToList();
        if (recipients.Count > 0)
            await _broadcaster.BroadcastCallCancelledAsync(
                recipients,
                new CallCancelledPayload(channelId)
            );

        var anyoneElseJoined = (await _voice.GetChannelParticipantsAsync(channelId)).Any(p =>
            p.UserId != userId
        );
        if (missed && wasLive && !anyoneElseJoined)
            await _messageService.PublishSystemMessageAsync(null, channelId, userId, "missed_call", "");

        _logger.LogDebug("User {UserId} cancelled ring on channel {ChannelId}", userId, channelId);
    }

    /// <summary>
    /// Declines a live ring: notifies the caller (their client hangs up in a 1:1; informational in a
    /// group) and dismisses the decliner's own other tabs. In a 1:1 the ring itself ends too — no one
    /// is left to answer; in a group the others keep ringing. Silent no-op when there's no live ring
    /// or the caller declines their own ring.
    /// </summary>
    public async Task DeclineCall(long channelId)
    {
        var userId = GetUserId();

        if (!await _dms.IsParticipantAsync(channelId, userId))
            return;

        var ringCaller = await _voice.GetRingCallerAsync(channelId);
        if (ringCaller is null || ringCaller == userId)
            return;

        await _broadcaster.BroadcastCallDeclinedAsync(
            ringCaller.Value,
            new CallDeclinedPayload(channelId, userId)
        );
        await _broadcaster.BroadcastCallCancelledAsync(
            [userId],
            new CallCancelledPayload(channelId)
        );

        if ((await _dms.GetParticipantIdsAsync(channelId)).Count == 2)
            await _voice.TryEndRingAsync(channelId);

        _logger.LogDebug("User {UserId} declined ring on channel {ChannelId}", userId, channelId);
    }

    // -------------------------------------------------------------------------
    // Group name helpers — used by HubBroadcaster too
    // -------------------------------------------------------------------------

    public static string ChannelGroup(long channelId) => $"channel:{channelId}";

    public static string GuildGroup(long guildId) => $"guild:{guildId}";

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private long GetUserId()
    {
        var claim =
            Context.User?.FindFirst(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirst("sub");

        if (claim is null || !long.TryParse(claim.Value, out var userId))
            throw new HubException("Authenticated user ID could not be resolved.");

        return userId;
    }
}
