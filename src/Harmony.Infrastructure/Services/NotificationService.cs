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
    private readonly INotificationSettingRepository _notificationSettings;
    private readonly IPresenceService _presence;
    private readonly IPushOutboxRepository _pushOutbox;
    private readonly IPushDispatchNudge _pushNudge;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        INotificationRepository notificationRepository,
        IUserBlockRepository userBlockRepository,
        IUserMuteRepository userMuteRepository,
        IHubBroadcaster hubBroadcaster,
        INotificationPreferenceRepository notificationPreferenceRepository,
        INotificationSettingRepository notificationSettingRepository,
        IPresenceService presence,
        IPushOutboxRepository pushOutboxRepository,
        IPushDispatchNudge pushNudge,
        ISnowflakeIdGenerator snowflake,
        ILogger<NotificationService> logger
    )
    {
        _notifications = notificationRepository;
        _userBlock = userBlockRepository;
        _userMute = userMuteRepository;
        _broadcaster = hubBroadcaster;
        _notificationPreferences = notificationPreferenceRepository;
        _notificationSettings = notificationSettingRepository;
        _presence = presence;
        _pushOutbox = pushOutboxRepository;
        _pushNudge = pushNudge;
        _snowflake = snowflake;
        _logger = logger;
    }

    /// <summary>
    /// Stages a PushOutbox row mirroring the notification, WITHOUT saving — it commits in
    /// the caller's SaveChangesAsync alongside the Notification row itself (transactional
    /// outbox: the push intent can never exist without the row, or vice versa). The
    /// dispatcher applies the offline/preference gates at send time.
    /// </summary>
    private async Task StagePushAsync(Notification notification)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _pushOutbox.AddAsync(
            new PushOutboxMessage
            {
                Id = _snowflake.NextId(),
                Kind = notification.Type,
                RecipientId = notification.UserId,
                ActorId = notification.ActorId,
                GuildId = notification.GuildId,
                ChannelId = notification.ChannelId,
                MessageId = notification.MessageId,
                NextAttemptAt = now,
                CreatedAt = now,
            }
        );
    }

    /// <summary>
    /// Pushes the live NotificationReceived event unless the recipient is currently in
    /// Do-Not-Disturb. DnD suppresses the real-time interruption only — the row is still
    /// persisted by the caller, so the user sees what they missed once they leave DnD.
    /// Any other status (online/away/offline) gets the live push as normal. Effective
    /// status is read fresh from presence; a DnD user who has since disconnected reads as
    /// "offline" and so is not suppressed.
    /// </summary>
    private async Task PushUnlessDndAsync(Notification notification, CancellationToken ct)
    {
        try
        {
            var status = await _presence.GetStatusAsync(notification.UserId, ct);
            if (string.Equals(status, "dnd", StringComparison.OrdinalIgnoreCase))
                return;

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

    /// <summary>
    /// Pushes the recipient's fresh unread-notification count so their bell badge updates without a
    /// refetch. Best-effort and fail-open — a badge that misses a push self-heals on the next
    /// GET /unread-count, so a broadcast failure must never bubble into the create path.
    /// </summary>
    private async Task BroadcastBadgeAsync(long userId, CancellationToken ct)
    {
        try
        {
            var count = await _notifications.GetUnreadCountAsync(userId);
            await _broadcaster.BroadcastNotificationBadgeAsync(userId, count, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast notification badge for UserId {UserId}", userId);
        }
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
        await StagePushAsync(notification);
        await _notifications.SaveChangesAsync();
        _pushNudge.Signal();

        await PushUnlessDndAsync(notification, ct);
        await BroadcastBadgeAsync(notification.UserId, ct);
    }

    /// <inheritdoc />
    public async Task CreateMentionNotificationsAsync(
        List<long> mentionedUserIds,
        long actorId,
        long? guildId,
        long channelId,
        long messageId,
        long createdAt,
        IReadOnlyCollection<long>? everyoneOriginIds = null,
        CancellationToken ct = default
    )
    {
        var prefRows = await _notificationPreferences.GetForUsersAsync(mentionedUserIds);
        var preferences = prefRows.ToDictionary(p => p.UserId);

        // Per-guild / per-channel notification level (guild messages only — DMs have no such scope).
        // Fetched once for the whole batch; channel-scope overrides guild-scope, absent = default.
        var channelLevels = new Dictionary<long, string>();
        var guildLevels = new Dictionary<long, string>();
        // Suppress-@everyone flag, resolved the same channel-over-guild way as the level.
        var channelSuppress = new Dictionary<long, bool>();
        var guildSuppress = new Dictionary<long, bool>();
        if (guildId.HasValue)
        {
            var settingRows = await _notificationSettings.GetForResolutionAsync(
                mentionedUserIds,
                guildId.Value,
                channelId
            );
            foreach (var row in settingRows)
            {
                if (row.ScopeType == NotificationScope.Channel)
                {
                    channelLevels[row.UserId] = row.Level;
                    channelSuppress[row.UserId] = row.SuppressEveryone;
                }
                else
                {
                    guildLevels[row.UserId] = row.Level;
                    guildSuppress[row.UserId] = row.SuppressEveryone;
                }
            }
        }

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

            // Resolved per-guild/channel level: channel-scope wins over guild-scope; absent = default.
            // "nothing" silences the whole scope (mentions included); "mentions"/"all" let a mention through.
            if (guildId.HasValue)
            {
                var level = channelLevels.TryGetValue(mentionedUserId, out var cl)
                    ? cl
                    : guildLevels.GetValueOrDefault(mentionedUserId, NotificationLevel.Default);
                if (level == NotificationLevel.Nothing)
                    continue;

                // Suppress-@everyone: a recipient reached ONLY through @everyone/@here who opted
                // out of broadcast pings in this scope is skipped (a direct @user/@role mention is
                // never in everyoneOriginIds, so it still notifies). Channel-scope wins over guild.
                if (everyoneOriginIds is not null && everyoneOriginIds.Contains(mentionedUserId))
                {
                    var suppress = channelSuppress.TryGetValue(mentionedUserId, out var cs)
                        ? cs
                        : guildSuppress.GetValueOrDefault(mentionedUserId, false);
                    if (suppress)
                        continue;
                }
            }

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
        {
            await _notifications.AddAsync(notification);
            await StagePushAsync(notification);
        }

        await _notifications.SaveChangesAsync();
        _pushNudge.Signal();

        foreach (var notification in toNotify)
        {
            await PushUnlessDndAsync(notification, ct);
            await BroadcastBadgeAsync(notification.UserId, ct);
        }
    }

    /// <inheritdoc />
    public async Task CreateReplyNotificationAsync(
        long recipientId,
        long actorId,
        long? guildId,
        long channelId,
        long messageId,
        long createdAt,
        CancellationToken ct = default
    )
    {
        if (recipientId == actorId)
            return;

        // Missing preference row = default enabled; only an explicit false suppresses.
        var pref = await _notificationPreferences.GetAsync(recipientId);
        if (pref is { RepliesEnabled: false })
            return;

        // Per-guild/channel level (guild only): channel-scope wins; "nothing" silences replies too.
        if (guildId.HasValue)
        {
            var settingRows = await _notificationSettings.GetForResolutionAsync(
                [recipientId],
                guildId.Value,
                channelId
            );
            string? channelLevel = null;
            string? guildLevel = null;
            foreach (var row in settingRows)
            {
                if (row.ScopeType == NotificationScope.Channel)
                    channelLevel = row.Level;
                else
                    guildLevel = row.Level;
            }
            if ((channelLevel ?? guildLevel ?? NotificationLevel.Default) == NotificationLevel.Nothing)
                return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (await _userMute.IsMutedAsync(recipientId, actorId, MuteTargetType.User, now))
            return;
        if (await _userMute.IsMutedAsync(recipientId, channelId, MuteTargetType.Channel, now))
            return;
        if (
            guildId.HasValue
            && await _userMute.IsMutedAsync(recipientId, guildId.Value, MuteTargetType.Guild, now)
        )
            return;
        if (await _userBlock.AreBlockedAsync(actorId, recipientId))
            return;

        var notification = new Notification
        {
            Id = _snowflake.NextId(),
            UserId = recipientId,
            Type = "reply",
            ActorId = actorId,
            GuildId = guildId,
            ChannelId = channelId,
            MessageId = messageId,
            IsRead = false,
            CreatedAt = createdAt,
        };

        await _notifications.AddAsync(notification);
        await StagePushAsync(notification);
        await _notifications.SaveChangesAsync();
        _pushNudge.Signal();

        await PushUnlessDndAsync(notification, ct);
        await BroadcastBadgeAsync(notification.UserId, ct);
    }

    /// <inheritdoc />
    public async Task CreateMessageNotificationsAsync(
        long actorId,
        long guildId,
        long channelId,
        long messageId,
        long createdAt,
        IReadOnlyCollection<long> alreadyNotifiedIds,
        CancellationToken ct = default
    )
    {
        // The opt-in set for the "all" level (channel-over-guild resolved in the repo). Default is
        // "mentions", so this is normally tiny or empty.
        var optedIn = await _notificationSettings.GetOptedIntoAllAsync(guildId, channelId);
        if (optedIn.Count == 0)
            return;

        var recipients = optedIn
            .Where(id => id != actorId && !alreadyNotifiedIds.Contains(id))
            .ToList();
        if (recipients.Count == 0)
            return;

        var prefRows = await _notificationPreferences.GetForUsersAsync(recipients);
        var preferences = prefRows.ToDictionary(p => p.UserId);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var toNotify = new List<Notification>();

        foreach (var recipientId in recipients)
        {
            // The master mentions switch doubles as the "all" gate — if a user turned off mention
            // notifications entirely, they don't want the noisier per-message ones either.
            if (preferences.TryGetValue(recipientId, out var pref) && !pref.MentionsEnabled)
                continue;

            if (await _userMute.IsMutedAsync(recipientId, actorId, MuteTargetType.User, now))
                continue;
            if (await _userMute.IsMutedAsync(recipientId, channelId, MuteTargetType.Channel, now))
                continue;
            if (await _userMute.IsMutedAsync(recipientId, guildId, MuteTargetType.Guild, now))
                continue;
            if (await _userBlock.AreBlockedAsync(actorId, recipientId))
                continue;

            toNotify.Add(
                new Notification
                {
                    Id = _snowflake.NextId(),
                    UserId = recipientId,
                    Type = "message",
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

        foreach (var notification in toNotify)
        {
            await _notifications.AddAsync(notification);
            await StagePushAsync(notification);
        }

        await _notifications.SaveChangesAsync();
        _pushNudge.Signal();

        foreach (var notification in toNotify)
        {
            await PushUnlessDndAsync(notification, ct);
            await BroadcastBadgeAsync(notification.UserId, ct);
        }
    }

    /// <inheritdoc />
    public async Task CreateGuildInviteNotificationAsync(
        long recipientId,
        long actorId,
        long guildId,
        CancellationToken ct = default
    )
    {
        if (recipientId == actorId)
            return;

        // Missing preference row = default enabled; only an explicit false suppresses.
        var pref = await _notificationPreferences.GetAsync(recipientId);
        if (pref is { GuildInvites: false })
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (await _userMute.IsMutedAsync(recipientId, actorId, MuteTargetType.User, now))
            return;
        if (await _userBlock.AreBlockedAsync(actorId, recipientId))
            return;

        var notification = new Notification
        {
            Id = _snowflake.NextId(),
            UserId = recipientId,
            Type = "guild_invite",
            ActorId = actorId,
            GuildId = guildId,
            IsRead = false,
            CreatedAt = now,
        };

        await _notifications.AddAsync(notification);
        await StagePushAsync(notification);
        await _notifications.SaveChangesAsync();
        _pushNudge.Signal();

        await PushUnlessDndAsync(notification, ct);
        await BroadcastBadgeAsync(notification.UserId, ct);
    }
}
