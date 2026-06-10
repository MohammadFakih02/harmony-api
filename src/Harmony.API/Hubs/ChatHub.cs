using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
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
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(IMessageService messageService, ILogger<ChatHub> logger)
    {
        _messageService = messageService;
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
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
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

    // -------------------------------------------------------------------------
    // Group management
    // -------------------------------------------------------------------------

    public async Task JoinChannel(long channelId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
        _logger.LogDebug("User {UserId} joined {Group}", GetUserId(), ChannelGroup(channelId));
    }

    public async Task LeaveChannel(long channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
        _logger.LogDebug("User {UserId} left {Group}", GetUserId(), ChannelGroup(channelId));
    }

    public async Task JoinGuild(long guildId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GuildGroup(guildId));
        _logger.LogDebug("User {UserId} joined {Group}", GetUserId(), GuildGroup(guildId));
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
                    MessageType: "text",
                    ReplyToId: null,
                    MentionIds: null,
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
