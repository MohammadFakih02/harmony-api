using System.Globalization;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// One-round-trip boot payload: aggregates what the client used to fetch with nine separate
/// requests at startup (profile, guilds, unread, friends, pending, DMs, nicknames,
/// notifications, notification badge). Read-only composition over the same repositories the
/// per-feature controllers use — their endpoints remain the refresh / fallback paths, and the
/// shared mappers are reused so the shapes cannot drift. Queries run sequentially: they share
/// the request's scoped DbContext, which is not thread-safe.
/// </summary>
[ApiController]
[Authorize]
[EnableRateLimiting("api")]
public class BootstrapController : HarmonyControllerBase
{
    private readonly IUserRepository _users;
    private readonly IGuildRepository _guilds;
    private readonly IChannelRepository _channels;
    private readonly IUnreadCountService _unread;
    private readonly IFriendRepository _friends;
    private readonly IDirectMessageRepository _dms;
    private readonly IUserNicknameRepository _nicknames;
    private readonly INotificationRepository _notifications;

    public BootstrapController(
        IUserRepository users,
        IGuildRepository guilds,
        IChannelRepository channels,
        IUnreadCountService unread,
        IFriendRepository friends,
        IDirectMessageRepository dms,
        IUserNicknameRepository nicknames,
        INotificationRepository notifications
    )
    {
        _users = users;
        _guilds = guilds;
        _channels = channels;
        _unread = unread;
        _friends = friends;
        _dms = dms;
        _nicknames = nicknames;
        _notifications = notifications;
    }

    // GET /api/users/me/bootstrap
    [HttpGet("api/users/me/bootstrap")]
    public async Task<IActionResult> Get()
    {
        var me = GetUserId();

        var user = await _users.GetByIdAsync(me);
        if (user is null)
            return NotFound();

        // Guilds (in the caller's personal rail order) + per-channel unread
        // (mirrors UsersController.GetMyGuilds / ReadStatesController.GetUnread).
        var guilds = UsersController.ApplyGuildOrder(
            await _guilds.GetByUserIdAsync(me),
            user.GuildOrder
        );
        var channelGuildMap = await _channels.GetTextChannelGuildMapAsync(guilds.Select(g => g.Id));
        var counts = await _unread.GetUnreadForUserAsync(me, channelGuildMap.Keys);

        // Friend rows (mirrors FriendsController.GetFriends / GetPending).
        var friendRows = await _friends.GetAcceptedAsync(me);
        var pendingRows = await _friends.GetPendingForAsync(me);

        // DM channel summaries + participants (mirrors DirectMessagesController.GetMyDms).
        var dmSummaries = await _dms.GetVisibleForUserAsync(me);
        var participantsByChannel = await _dms.GetParticipantsForChannelsAsync(
            dmSummaries.Select(s => s.ChannelId)
        );

        // One batch resolve for every user identity the payload references (friends, pending,
        // DM participants) — the standalone endpoints do three separate batches.
        var referencedUserIds = friendRows
            .Select(r => FriendsController.Other(r, me))
            .Concat(pendingRows.Select(r => FriendsController.Other(r, me)))
            .Concat(participantsByChannel.Values.SelectMany(ids => ids).Where(id => id != me))
            .Distinct();
        var usersById = await _users.GetByIdsAsync(referencedUserIds);

        var nicknameRows = await _nicknames.GetByOwnerAsync(me);
        var notifications = await _notifications.GetForUserAsync(me, 20);
        var notificationUnreadCount = await _notifications.GetUnreadCountAsync(me);

        return Ok(
            new BootstrapResponse(
                UsersController.ToProfileResponse(user),
                guilds.Select(g => new GuildResponse(
                    g.Id,
                    g.Name,
                    g.Description,
                    g.OwnerId,
                    g.IconKey,
                    g.BannerKey,
                    g.IsPublic,
                    g.MemberCount,
                    g.CreatedAt,
                    g.WelcomeChannelId,
                    g.WelcomeMessage,
                    g.SystemMessagesEnabled,
                    g.RequireVerifiedEmail
                )),
                counts.Select(kv => new UnreadCountResponse(kv.Key, channelGuildMap[kv.Key], kv.Value)),
                friendRows
                    .Where(r => usersById.ContainsKey(FriendsController.Other(r, me)))
                    .Select(r =>
                    {
                        var u = usersById[FriendsController.Other(r, me)];
                        return new FriendResponse(u.Id, u.UserName!, u.AvatarKey, u.BannerKey, r.UpdatedAt);
                    }),
                pendingRows
                    .Where(r => usersById.ContainsKey(FriendsController.Other(r, me)))
                    .Select(r =>
                    {
                        var u = usersById[FriendsController.Other(r, me)];
                        return new PendingFriendResponse(
                            u.Id,
                            u.UserName!,
                            u.AvatarKey,
                            u.BannerKey,
                            r.RequesterId == me ? "outgoing" : "incoming",
                            r.CreatedAt
                        );
                    }),
                dmSummaries.Select(s =>
                    DirectMessagesController.BuildResponse(s, participantsByChannel, usersById, me)
                ),
                nicknameRows.ToDictionary(
                    n => n.TargetId.ToString(CultureInfo.InvariantCulture),
                    n => n.Nickname
                ),
                notifications.Select(n => new NotificationResponse(
                    n.Id,
                    n.Type,
                    n.ActorId,
                    n.GuildId,
                    n.ChannelId,
                    n.MessageId,
                    n.IsRead,
                    n.CreatedAt
                )),
                notificationUnreadCount
            )
        );
    }

}
