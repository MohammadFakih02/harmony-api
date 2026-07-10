using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
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
    private readonly IDirectMessageRepository _dms;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IMessageService messageService,
        IGuildRepository guildRepository,
        IChannelRepository channelRepository,
        IPermissionService permissions,
        IPresenceService presence,
        IVoiceStateService voice,
        IDirectMessageRepository dms,
        IHubBroadcaster broadcaster,
        ILogger<ChatHub> logger
    )
    {
        _messageService = messageService;
        _guildRepository = guildRepository;
        _channelRepository = channelRepository;
        _permissions = permissions;
        _presence = presence;
        _voice = voice;
        _dms = dms;
        _broadcaster = broadcaster;
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
        await _presence.HeartbeatAsync(GetUserId());
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

        await _voice.JoinAsync(channelId, channel.GuildId, userId);
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
    /// Whether a user may connect to voice in a channel: guild channel → ConnectVoice (overrides
    /// applied); guild-less DM → the caller must be a participant. Mirrors CanAccessChannelAsync but
    /// with the voice permission bit instead of ViewChannel.
    /// </summary>
    private async Task<bool> CanConnectVoiceAsync(long userId, Channel channel) =>
        channel.GuildId is { } guildId
            ? await _permissions.HasAsync(userId, guildId, Permission.ConnectVoice, channel.Id)
            : await _dms.IsParticipantAsync(channel.Id, userId);

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
