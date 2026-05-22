using System.Security.Claims;
using Harmony.Core.Interfaces;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Harmony.API.Hubs;

[Authorize]
public class ChatHub : Hub<IChatClient>
{
    private readonly IMessagePublisher _publisher;
    private readonly IGuildRepository _guilds;
    private readonly IChannelRepository _channels;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IMessagePublisher publisher,
        IGuildRepository guilds,
        IChannelRepository channels,
        ISnowflakeIdGenerator snowflake,
        IConnectionMultiplexer redis,
        ILogger<ChatHub> logger
    )
    {
        _publisher = publisher;
        _guilds = guilds;
        _channels = channels;
        _snowflake = snowflake;
        _redis = redis;
        _logger = logger;
    }

    // ------------------------------------------------------------------ lifecycle

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        var db = _redis.GetDatabase();

        // Track connection in Redis SET — supports multiple tabs / devices
        await db.SetAddAsync($"session:{userId}", Context.ConnectionId);

        // Presence — TTL refreshed by Heartbeat()
        await db.StringSetAsync($"user:{userId}:status", "online", TimeSpan.FromSeconds(60));
        await db.SortedSetAddAsync(
            "presence:online",
            userId.ToString(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );

        // Add to personal group so server can push to this user across connections
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));

        _logger.LogInformation(
            "User {UserId} connected ({ConnectionId})",
            userId,
            Context.ConnectionId
        );

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        var db = _redis.GetDatabase();

        await db.SetRemoveAsync($"session:{userId}", Context.ConnectionId);

        // Only mark offline when last connection closes
        var remaining = await db.SetLengthAsync($"session:{userId}");
        if (remaining == 0)
        {
            await db.KeyDeleteAsync($"user:{userId}:status");
            await db.SortedSetRemoveAsync("presence:online", userId.ToString());

            // Broadcast offline to guilds — consumers of UnreadCountUpdated
            // Full guild fan-out deferred to Month 3 (guild membership cache needed)
            _logger.LogInformation("User {UserId} is now offline", userId);
        }

        _logger.LogInformation(
            "User {UserId} disconnected ({ConnectionId})",
            userId,
            Context.ConnectionId
        );

        await base.OnDisconnectedAsync(exception);
    }

    // ------------------------------------------------------------------ channel / guild groups

    [EnableRateLimiting("signalr")]
    public async Task JoinChannel(long channelId)
    {
        var userId = GetUserId();

        if (!await IsMemberOfChannelGuild(userId, channelId))
        {
            await Clients.Caller.Error("Not a member of this channel's guild.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
    }

    [EnableRateLimiting("signalr")]
    public async Task LeaveChannel(long channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChannelGroup(channelId));
    }

    [EnableRateLimiting("signalr")]
    public async Task JoinGuild(long guildId)
    {
        var userId = GetUserId();

        var isMember = await _guilds.IsMemberAsync(guildId, userId);
        if (!isMember)
        {
            await Clients.Caller.Error("Not a member of this guild.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GuildGroup(guildId));
    }

    // ------------------------------------------------------------------ messaging

    [EnableRateLimiting("signalr")]
    public async Task SendMessage(SendMessageRequest request)
    {
        var userId = GetUserId();

        if (!await IsMemberOfChannelGuild(userId, request.ChannelId))
        {
            await Clients.Caller.Error("Not a member of this channel's guild.");
            return;
        }

        var messageId = _snowflake.NextId();
        var now = DateTimeOffset.UtcNow;

        // Fast path — broadcast immediately via Redis backplane to channel group
        // Do NOT await publisher before broadcasting (dual-path design)
        var broadcastTask = Clients
            .Group(ChannelGroup(request.ChannelId))
            .MessageReceived(
                new MessageReceivedPayload(
                    MessageId: messageId,
                    ChannelId: request.ChannelId,
                    GuildId: request.GuildId,
                    UserId: userId,
                    Username: GetUsername(),
                    AvatarKey: null, // populated by client from store
                    Content: request.Content,
                    MessageType: "text",
                    ReplyToId: request.ReplyToId,
                    AttachmentIds: request.AttachmentIds ?? [],
                    MentionIds: request.MentionIds ?? [],
                    SentAt: now.ToUnixTimeMilliseconds()
                )
            );

        // Slow path — async persistence via RabbitMQ
        var publishTask = _publisher.PublishMessageSentAsync(
            new MessageSentEvent(
                MessageId: messageId,
                ChannelId: request.ChannelId,
                GuildId: request.GuildId,
                UserId: userId,
                Content: request.Content,
                MessageType: "text",
                AttachmentIds: request.AttachmentIds ?? [],
                MentionIds: request.MentionIds ?? [],
                ReplyToId: request.ReplyToId,
                SentAt: now
            )
        );

        await Task.WhenAll(broadcastTask, publishTask);
    }

    [EnableRateLimiting("signalr")]
    public async Task EditMessage(EditMessageRequest request)
    {
        var userId = GetUserId();

        if (!await IsMemberOfChannelGuild(userId, request.ChannelId))
        {
            await Clients.Caller.Error("Not a member of this channel's guild.");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var broadcastTask = Clients
            .Group(ChannelGroup(request.ChannelId))
            .MessageEdited(
                new MessageEditedPayload(
                    MessageId: request.MessageId,
                    ChannelId: request.ChannelId,
                    GuildId: request.GuildId,
                    NewContent: request.NewContent,
                    EditedAt: now.ToUnixTimeMilliseconds()
                )
            );

        var publishTask = _publisher.PublishMessageEditedAsync(
            new MessageEditedEvent(
                MessageId: request.MessageId,
                ChannelId: request.ChannelId,
                GuildId: request.GuildId,
                EditedByUserId: userId,
                NewContent: request.NewContent,
                EditedAt: now
            )
        );

        await Task.WhenAll(broadcastTask, publishTask);
    }

    [EnableRateLimiting("signalr")]
    public async Task DeleteMessage(DeleteMessageRequest request)
    {
        var userId = GetUserId();

        if (!await IsMemberOfChannelGuild(userId, request.ChannelId))
        {
            await Clients.Caller.Error("Not a member of this channel's guild.");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var broadcastTask = Clients
            .Group(ChannelGroup(request.ChannelId))
            .MessageDeleted(
                new MessageDeletedPayload(
                    MessageId: request.MessageId,
                    ChannelId: request.ChannelId,
                    GuildId: request.GuildId
                )
            );

        var publishTask = _publisher.PublishMessageDeletedAsync(
            new MessageDeletedEvent(
                MessageId: request.MessageId,
                ChannelId: request.ChannelId,
                GuildId: request.GuildId,
                DeletedByUserId: userId,
                DeletedAt: now
            )
        );

        await Task.WhenAll(broadcastTask, publishTask);
    }

    // ------------------------------------------------------------------ typing

    [EnableRateLimiting("signalr")]
    public async Task StartTyping(long channelId, long guildId)
    {
        var userId = GetUserId();

        await Clients
            .OthersInGroup(ChannelGroup(channelId))
            .TypingStarted(new TypingPayload(userId, GetUsername(), channelId, guildId));
    }

    [EnableRateLimiting("signalr")]
    public async Task StopTyping(long channelId, long guildId)
    {
        var userId = GetUserId();

        await Clients
            .OthersInGroup(ChannelGroup(channelId))
            .TypingStopped(new TypingPayload(userId, GetUsername(), channelId, guildId));
    }

    // ------------------------------------------------------------------ heartbeat

    [EnableRateLimiting("signalr")]
    public async Task Heartbeat()
    {
        var userId = GetUserId();
        var db = _redis.GetDatabase();

        // Slide presence TTL — client sends every 30s
        await db.StringSetAsync($"user:{userId}:status", "online", TimeSpan.FromSeconds(60));
        await db.SortedSetAddAsync(
            "presence:online",
            userId.ToString(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );
    }

    // ------------------------------------------------------------------ helpers

    private long GetUserId() =>
        long.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string GetUsername() => Context.User!.FindFirstValue(ClaimTypes.Name)!;

    private static string ChannelGroup(long channelId) => $"channel:{channelId}";

    private static string GuildGroup(long guildId) => $"guild:{guildId}";

    private static string UserGroup(long userId) => $"user:{userId}";

    private async Task<bool> IsMemberOfChannelGuild(long userId, long channelId)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null)
            return false;
        if (channel.GuildId is null)
            return false; // DM channels handled separately in Month 3
        return await _guilds.IsMemberAsync(channel.GuildId.Value, userId);
    }
}

// ------------------------------------------------------------------ inbound request records

public record SendMessageRequest(
    long ChannelId,
    long GuildId,
    string Content,
    long? ReplyToId,
    List<long>? AttachmentIds,
    List<long>? MentionIds
);

public record EditMessageRequest(long MessageId, long ChannelId, long GuildId, string NewContent);

public record DeleteMessageRequest(long MessageId, long ChannelId, long GuildId);
