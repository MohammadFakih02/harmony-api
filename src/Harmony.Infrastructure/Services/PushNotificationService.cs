using System.Text.Json;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Dispatches the PushOutbox: wakes on a producer nudge (or every 5s as the crash-recovery
/// backstop), drains all due rows, and web-pushes each to the recipient's registered
/// browsers — but ONLY when they're offline (no SignalR connection); a connected user
/// already got the live event. At-least-once: a row survives until it's dispatched or
/// exhausts its retries, so pushes staged just before a crash go out after the restart.
/// Duplicate deliveries (crash between send and delete) collapse client-side via the
/// notification tag. Scope-per-cycle like the other sweeps; the per-row pipeline is
/// exposed as <see cref="ProcessAsync"/> for direct invocation in tests.
/// </summary>
public class PushNotificationService : BackgroundService
{
    public const int MaxAttempts = 5;
    private const int BatchSize = 32;
    private const int PreviewMaxLength = 140;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPushDispatchNudge _nudge;
    private readonly IWebPushSender _sender;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public PushNotificationService(
        IServiceScopeFactory scopeFactory,
        IPushDispatchNudge nudge,
        IWebPushSender sender,
        ILogger<PushNotificationService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _nudge = nudge;
        _sender = sender;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PushNotificationService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _nudge.WaitAsync(_pollInterval, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the push-outbox dispatch cycle.");
            }
        }

        _logger.LogInformation("PushNotificationService background worker stopped.");
    }

    /// <summary>Drains every currently-due outbox row. Returns the number of rows handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var handled = 0;
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IPushOutboxRepository>();

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var due = await outbox.GetDueAsync(now, BatchSize);
            if (due.Count == 0)
                return handled;

            foreach (var row in due)
            {
                try
                {
                    await ProcessAsync(scope.ServiceProvider, row, ct);
                    outbox.Remove(row);
                }
                catch (Exception ex)
                {
                    // Transient (or unexpected) failure — keep the row and back off; give up
                    // after MaxAttempts so one poisoned row can't cycle forever.
                    row.Attempts++;
                    if (row.Attempts >= MaxAttempts)
                    {
                        _logger.LogWarning(
                            ex,
                            "Push outbox row {Id} ({Kind}) dead after {Attempts} attempts — dropping",
                            row.Id,
                            row.Kind,
                            row.Attempts
                        );
                        outbox.Remove(row);
                    }
                    else
                    {
                        row.NextAttemptAt =
                            now + (long)(Math.Pow(2, row.Attempts) * 30_000);
                        _logger.LogWarning(
                            ex,
                            "Push outbox row {Id} ({Kind}) failed attempt {Attempts} — retrying later",
                            row.Id,
                            row.Kind,
                            row.Attempts
                        );
                    }
                }
                handled++;
            }

            await outbox.SaveChangesAsync();
        }
        return handled;
    }

    /// <summary>
    /// The per-row pipeline: resolve recipients, gate each (offline + push-enabled + not
    /// DnD, plus mute/block for the un-suppression-checked "dm" kind), compose the payload,
    /// send to every registered browser, and prune subscriptions the push service reports
    /// gone. A recipient with no subscriptions or a failed send is skipped silently — only
    /// an exception (treated as transient) makes the caller retry the row.
    /// </summary>
    public async Task ProcessAsync(
        IServiceProvider services,
        PushOutboxMessage row,
        CancellationToken ct = default
    )
    {
        var presence = services.GetRequiredService<IPresenceService>();
        var subscriptions = services.GetRequiredService<IPushSubscriptionRepository>();
        var preferences = services.GetRequiredService<INotificationPreferenceRepository>();

        var recipients = await ResolveRecipientsAsync(services, row);
        if (recipients.Count == 0)
            return;

        string? payload = null; // composed lazily — most rows short-circuit on the gates

        foreach (var recipientId in recipients)
        {
            if (await presence.IsConnectedAsync(recipientId, ct))
                continue; // they got the live event — push is offline delivery only

            // An offline user whose durable preferred status is DnD stays quiet, matching
            // the live path's PushUnlessDndAsync posture.
            var preferred = await presence.GetPreferredStatusAsync(recipientId, ct);
            if (string.Equals(preferred, "dnd", StringComparison.OrdinalIgnoreCase))
                continue;

            // Missing preference row = every flag enabled, same contract as NotificationService.
            var pref = await preferences.GetAsync(recipientId);
            if (pref is { PushEnabled: false })
                continue;

            // "dm"/"call" rows never went through NotificationService's suppression chain
            // (there is no Notification row for a plain DM message or a ring) — apply
            // mute/block here. The other kinds already survived the full chain when staged.
            if (
                row.Kind is PushKind.Dm or PushKind.Call
                && await IsDmSuppressedAsync(services, row, recipientId)
            )
                continue;

            var subs = await subscriptions.GetForUserAsync(recipientId);
            if (subs.Count == 0)
                continue;

            payload ??= await ComposePayloadAsync(services, row, ct);

            foreach (var sub in subs)
            {
                var result = await _sender.SendAsync(
                    sub.Endpoint,
                    sub.P256dh,
                    sub.AuthKey,
                    payload,
                    ct
                );
                if (result == PushSendResult.Gone)
                    subscriptions.Remove(sub); // saved with the outbox row by the caller
            }
        }
    }

    private static async Task<List<long>> ResolveRecipientsAsync(
        IServiceProvider services,
        PushOutboxMessage row
    )
    {
        if (row.Kind is not (PushKind.Dm or PushKind.Call))
            return [row.RecipientId];

        if (row.ChannelId is not { } channelId)
            return [];

        var dms = services.GetRequiredService<IDirectMessageRepository>();
        var participants = await dms.GetParticipantIdsAsync(channelId);

        var excluded = new HashSet<long>();
        if (row.ActorId is { } actorId)
            excluded.Add(actorId);
        if (!string.IsNullOrEmpty(row.ExcludeUserIds))
            foreach (var part in row.ExcludeUserIds.Split(','))
                if (long.TryParse(part, out var id))
                    excluded.Add(id);

        return participants.Where(id => !excluded.Contains(id)).ToList();
    }

    private static async Task<bool> IsDmSuppressedAsync(
        IServiceProvider services,
        PushOutboxMessage row,
        long recipientId
    )
    {
        var mutes = services.GetRequiredService<IUserMuteRepository>();
        var blocks = services.GetRequiredService<IUserBlockRepository>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (
            row.ActorId is { } actorId
            && (
                await mutes.IsMutedAsync(recipientId, actorId, MuteTargetType.User, now)
                || await blocks.AreBlockedAsync(actorId, recipientId)
            )
        )
            return true;

        return row.ChannelId is { } channelId
            && await mutes.IsMutedAsync(recipientId, channelId, MuteTargetType.Channel, now);
    }

    private async Task<string> ComposePayloadAsync(
        IServiceProvider services,
        PushOutboxMessage row,
        CancellationToken ct
    )
    {
        var users = services.GetRequiredService<IUserRepository>();
        var actorName = "Someone";
        if (row.ActorId is { } actorId)
            actorName = (await users.GetByIdAsync(actorId))?.UserName ?? actorName;

        string? channelName = null;
        if (row.GuildId is not null && row.ChannelId is { } channelId)
        {
            var channels = services.GetRequiredService<IChannelRepository>();
            channelName = (await channels.GetByIdAsync(channelId))?.Name;
        }

        string? preview = null;
        if (row.MessageId is { } messageId)
        {
            var messages = services.GetRequiredService<IMessageRepository>();
            var message = await messages.GetByIdAsync(messageId, ct);
            if (message is { IsDeleted: false } && !string.IsNullOrWhiteSpace(message.Content))
                preview =
                    message.Content.Length <= PreviewMaxLength
                        ? message.Content
                        : message.Content[..PreviewMaxLength] + "…";
        }

        var place = channelName is not null ? $"#{channelName}" : "a direct message";
        var (title, body) = row.Kind switch
        {
            PushKind.Mention => ($"{actorName} mentioned you in {place}", preview ?? ""),
            PushKind.Reply => ($"{actorName} replied to you in {place}", preview ?? ""),
            PushKind.Message => ($"{actorName} posted in {place}", preview ?? ""),
            PushKind.FriendRequest => ($"{actorName} sent you a friend request", ""),
            PushKind.GuildInvite => ($"{actorName} invited you to a server", "Tap to view the invite"),
            PushKind.Call => ($"{actorName} is calling you", "Incoming call — tap to open Harmony"),
            _ => (actorName, preview ?? "Sent you a message"),
        };

        var url = row.Kind switch
        {
            PushKind.FriendRequest => "/app/friends",
            PushKind.GuildInvite when row.GuildId is { } inviteGuild => $"/app/guilds/{inviteGuild}",
            _ when row.GuildId is { } guildId && row.ChannelId is { } chId =>
                $"/app/guilds/{guildId}/channels/{chId}",
            _ when row.ChannelId is { } chId => $"/app/dm/{chId}",
            _ => "/app/friends",
        };

        // Same-tag notifications replace each other in the OS tray — repeated pushes from
        // one conversation (and at-least-once duplicates) collapse instead of stacking.
        // Rings get their own tag so a later message push can't swallow "X is calling you".
        var tag = row.ChannelId is { } tagChannel
            ? row.Kind == PushKind.Call
                ? $"call-{tagChannel}"
                : $"channel-{tagChannel}"
            : $"friend-{row.ActorId}";

        return JsonSerializer.Serialize(new { title, body, url, tag });
    }
}
