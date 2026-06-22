using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// EF + Redis-backed implementation of <see cref="INotificationService"/>. Both methods
/// follow the same shape: cheapest checks first (self-action, then the in-memory
/// preference lookup) before the mute/block round trips, persist whatever survives in a
/// single batch, then fan out the live push with each broadcast individually try/caught —
/// one dead connection can't poison the rest of the batch or bubble into the caller
/// (same fail-open fan-out shape as RedisUnreadCountService).
/// </summary>
public class NotificationService : INotificationService
{
    private readonly INotificationRepository _notifications;
    private readonly IUserBlockRepository _userBlock;
    private readonly IUserMuteRepository _userMute;
    private readonly IHubBroadcaster _broadcaster;
    private readonly INotificationPreferenceRepository _notificationPreferences;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        INotificationRepository notificationRepository,
        IUserBlockRepository userBlockRepository,
        IUserMuteRepository userMuteRepository,
        IHubBroadcaster hubBroadcaster,
        INotificationPreferenceRepository notificationPreferenceRepository,
        ISnowflakeIdGenerator snowflake,
        ILogger<NotificationService> logger
    )
    {
        _notifications = notificationRepository;
        _userBlock = userBlockRepository;
        _userMute = userMuteRepository;
        _broadcaster = hubBroadcaster;
        _notificationPreferences = notificationPreferenceRepository;
        _snowflake = snowflake;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CreateFriendRequestNotificationAsync(
        long addresseeId,
        long requesterId,
        CancellationToken ct = default
    )
    {
        // No preference row = every flag defaults to enabled (see GetAsync), so a null
        // result here means "allowed," not "blocked" — only an explicit false suppresses.
        var preferences = await _notificationPreferences.GetAsync(addresseeId);
        if (preferences != null)
        {
            if (!preferences.FriendRequests)
            {
                return;
            }
        }
        // Mute check is addressee-mutes-requester, not the reverse: this notification
        // belongs to the addressee, so it's their mute list that can suppress it.
        if (
            await _userMute.IsMutedAsync(
                addresseeId,
                requesterId,
                MuteTargetType.User,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            )
        )
        {
            return;
        }
        if (await _userBlock.AreBlockedAsync(addresseeId, requesterId))
        {
            return;
        }
        Notification notification = new Notification
        {
            Id = _snowflake.NextId(),
            UserId = addresseeId,
            Type = "friend_request",
            ActorId = requesterId,
            IsRead = false,
        };

        await _notifications.AddAsync(notification);
        await _notifications.SaveChangesAsync();

        try
        {
            await _broadcaster.BroadcastNotificationReceivedAsync(
                notification.UserId,
                new NotificationPayload(
                    notification.Id,
                    notification.Type,
                    notification.ActorId,
                    notification.GuildId,
                    notification.ChannelId,
                    notification.MessageId,
                    notification.CreatedAt
                ),
                ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to push NotificationReceived for UserId {UserId}",
                notification.UserId
            );
        }
    }

    /// <inheritdoc />
    public async Task CreateMentionNotificationsAsync(
        List<long> mentionedUserIds,
        long actorId,
        long? guildId,
        long channelId,
        long messageId,
        long createdAt,
        CancellationToken ct = default
    )
    {
        var prefRows = await _notificationPreferences.GetForUsersAsync(mentionedUserIds);
        var preferences = prefRows.ToDictionary(p => p.UserId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var toNotify = new List<Notification>();

        foreach (var mentionedUserId in mentionedUserIds)
        {
            if (mentionedUserId == actorId)
                continue;

            // TryGetValue, not the indexer: a missing row means "default enabled" (see
            // GetForUsersAsync), not "key not found" — must not throw for that user.
            if (preferences.TryGetValue(mentionedUserId, out var pref) && !pref.MentionsEnabled)
                continue;

            if (await _userMute.IsMutedAsync(mentionedUserId, actorId, MuteTargetType.User, now))
                continue;

            if (
                await _userMute.IsMutedAsync(
                    mentionedUserId,
                    channelId,
                    MuteTargetType.Channel,
                    now
                )
            )
                continue;

            // Guild-less (DM) channels have no guild mute to check — guildId is null there.
            if (
                guildId.HasValue
                && await _userMute.IsMutedAsync(
                    mentionedUserId,
                    guildId.Value,
                    MuteTargetType.Guild,
                    now
                )
            )
                continue;

            if (await _userBlock.AreBlockedAsync(actorId, mentionedUserId))
                continue;

            toNotify.Add(
                new Notification
                {
                    Id = _snowflake.NextId(),
                    UserId = mentionedUserId,
                    Type = "mention",
                    ActorId = actorId,
                    GuildId = guildId,
                    ChannelId = channelId,
                    MessageId = messageId,
                    IsRead = false,
                    CreatedAt = createdAt,
                }
            );
        }

        if (toNotify.Count == 0)
            return;

        // Single SaveChangesAsync for the whole batch — one round trip regardless of
        // how many recipients survived the suppression chain above.
        foreach (var notification in toNotify)
            await _notifications.AddAsync(notification);

        await _notifications.SaveChangesAsync();

        foreach (var notification in toNotify)
        {
            try
            {
                await _broadcaster.BroadcastNotificationReceivedAsync(
                    notification.UserId,
                    new NotificationPayload(
                        notification.Id,
                        notification.Type,
                        notification.ActorId,
                        notification.GuildId,
                        notification.ChannelId,
                        notification.MessageId,
                        notification.CreatedAt
                    ),
                    ct
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to push NotificationReceived for UserId {UserId}",
                    notification.UserId
                );
            }
        }
    }
}
