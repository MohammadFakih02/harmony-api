using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Messaging;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// Grain-memory implementation of <see cref="IChannelGrain"/> — the D2a broadcast-first path. Where
/// the pre-D2a pipeline broadcast a new message only <em>after</em> the async consumer had persisted
/// it to Scylla (the serial-dispatch ceiling k6 measured), this grain fans the message out to
/// SignalR the instant it is sent, and lets the durable RabbitMQ persist log catch up off the hot
/// path.
///
/// <para><b>Ordering:</b> broadcast → publish (the confirmed design). Peers receive the message
/// before it touches RabbitMQ, so a broker hiccup can't stall live delivery. The only exposure is a
/// grain crash in the microsecond window between the broadcast and the publish — a phantom the client
/// self-heals on its next snowflake-cursor refetch (the message simply isn't there). A silo restart
/// is safe: SignalR connections drop and clients refetch from Scylla, and any message already queued
/// for persist still lands from the durable queue.</para>
///
/// <para><b>Layering:</b> the singletons (<see cref="IHubBroadcaster"/>, <see cref="IMessagePublisher"/>,
/// <see cref="IUserDisplayCache"/>) are injected directly; only the scoped
/// <see cref="IUserRepository"/> fallback is resolved through an <see cref="IServiceScopeFactory"/>,
/// since a grain outlives any request scope. The display resolve + response build are the shared
/// <see cref="MessageSendFanout"/> helpers, so the grain and the consumer can't drift.</para>
/// </summary>
public sealed class ChannelGrain : Grain, IChannelGrain
{
    private readonly IHubBroadcaster _broadcaster;
    private readonly IMessagePublisher _publisher;
    private readonly IUserDisplayCache _displayCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChannelGrain> _logger;

    public ChannelGrain(
        IHubBroadcaster broadcaster,
        IMessagePublisher publisher,
        IUserDisplayCache displayCache,
        IServiceScopeFactory scopeFactory,
        ILogger<ChannelGrain> logger
    )
    {
        _broadcaster = broadcaster;
        _publisher = publisher;
        _displayCache = displayCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task SendMessage(MessageSentEvent evt)
    {
        // 1. Resolve the sender's render fields (Redis cache + repo fallback) and build the
        //    authoritative response. The repo is scoped, so a scope is opened just for the fallback.
        UserDisplay display;
        using (var scope = _scopeFactory.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            display = await MessageSendFanout.ResolveSenderDisplayAsync(
                _displayCache, users, evt.UserId, _logger);
        }

        // 2. BROADCAST FIRST — peers see the message before it touches the persist log.
        await _broadcaster.BroadcastMessageReceivedAsync(MessageSendFanout.BuildReceived(evt, display));

        // 3. Publish to RabbitMQ as the durable persist log, flagged so the consumer persists +
        //    fans out unread but does NOT re-broadcast (this grain already did).
        await _publisher.PublishMessageSentAsync(evt with { BroadcastAlready = true });
    }

    public async Task EditMessage(MessageEditedEvent evt)
    {
        // Content is already persisted to Scylla synchronously by MessageService, so no display
        // resolve and no durability window — just fan the edit out, then republish the event flagged
        // so the consumer runs its async work (newly-added-mention notifications) without re-broadcasting.
        await _broadcaster.BroadcastMessageEditedAsync(
            new MessageEditedPayload(
                MessageId: evt.MessageId,
                ChannelId: evt.ChannelId,
                GuildId: evt.GuildId,
                EditedByUserId: evt.EditedByUserId,
                NewContent: evt.NewContent,
                EditedAt: evt.EditedAt.ToUnixTimeMilliseconds()));

        await _publisher.PublishMessageEditedAsync(evt with { BroadcastAlready = true });
    }

    public async Task DeleteMessage(MessageDeletedEvent evt)
    {
        // Soft-delete is already persisted to Scylla synchronously by MessageService. Fan the
        // deletion out now, then republish flagged so the consumer's async work runs without re-broadcasting.
        await _broadcaster.BroadcastMessageDeletedAsync(
            new MessageDeletedPayload(
                MessageId: evt.MessageId,
                ChannelId: evt.ChannelId,
                GuildId: evt.GuildId,
                DeletedByUserId: evt.DeletedByUserId,
                DeletedAt: evt.DeletedAt.ToUnixTimeMilliseconds()));

        await _publisher.PublishMessageDeletedAsync(evt with { BroadcastAlready = true });
    }
}
