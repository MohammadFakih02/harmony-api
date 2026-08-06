using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="IUnreadCountService"/>. Counts live at
/// unread:{userId}:{channelId} as a cache; read_states (Scylla) is truth.
/// Two fail-open layers: Redis-down short-circuits; per-member broadcast is
/// individually guarded so one bad push can't poison the fan-out or the ack.
/// </summary>
public sealed class RedisUnreadCountService : IUnreadCountService
{
    private readonly IRedisConnectionProvider _redisProvider;
    private readonly IGuildRepository _guildRepository;
    private readonly IReadStateRepository _readStateRepository;
    private readonly IHubBroadcaster _broadcaster;
    private readonly IPermissionService _permissions;
    private readonly IDirectMessageRepository _dms;
    private readonly ILogger<RedisUnreadCountService> _logger;

    public RedisUnreadCountService(
        IRedisConnectionProvider redisProvider,
        IGuildRepository guildRepository,
        IReadStateRepository readStateRepository,
        IHubBroadcaster broadcaster,
        IPermissionService permissions,
        IDirectMessageRepository dms,
        ILogger<RedisUnreadCountService> logger
    )
    {
        _redisProvider = redisProvider;
        _guildRepository = guildRepository;
        _readStateRepository = readStateRepository;
        _broadcaster = broadcaster;
        _permissions = permissions;
        _dms = dms;
        _logger = logger;
    }

    public async Task IncrementForChannelAsync(
        long? guildId,
        long channelId,
        long senderUserId,
        CancellationToken ct = default
    )
    {
        if (!_redisProvider.IsConnected)
        {
            _logger.LogDebug(
                "Unread: Redis unavailable — skipping increment for channel {ChannelId}",
                channelId
            );
            return;
        }

        // Recipient resolution branches on guild: visible guild members, or DM participants.
        var recipientIds = await ResolveRecipientIdsAsync(guildId, channelId, senderUserId);
        if (recipientIds.Count == 0)
            return;

        List<(long userId, Task<long> incr)> pending;
        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var batch = db.CreateBatch();
            pending = recipientIds
                .Select(uid =>
                    (userId: uid, incr: batch.StringIncrementAsync(UnreadKey(uid, channelId)))
                )
                .ToList();
            batch.Execute(); // single round-trip — all INCRs flushed together
            await Task.WhenAll(pending.Select(p => p.incr));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unread: pipelined INCR failed for channel {ChannelId} — skipping fan-out",
                channelId
            );
            return;
        }

        // Dispatched together, not one-await-at-a-time. Each send is an independent trip through the
        // Redis backplane, so awaiting them in sequence stacked ~54 round-trips onto every message
        // (~17ms measured — the same order as the resolve loop above). IHubContext is built for
        // concurrent use and the broadcaster holds nothing per-call, so this is safe; the per-user
        // try/catch stays INSIDE the task so one failed recipient still can't take out the rest.
        await Task.WhenAll(
            pending.Select(async p =>
            {
                try
                {
                    await _broadcaster.BroadcastUnreadCountAsync(
                        p.userId,
                        new UnreadCountPayload(channelId, guildId, (int)p.incr.Result),
                        ct
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Unread: broadcast failed for user {UserId} on channel {ChannelId} — continuing",
                        p.userId,
                        channelId
                    );
                }
            })
        );
    }

    public async Task MarkReadAsync(
        long userId,
        long? guildId,
        long channelId,
        long lastReadMessageId,
        CancellationToken ct = default
    )
    {
        // 1. Truth first — NOT swallowed. If this throws, mark-as-read genuinely failed.
        await _readStateRepository.MarkAsReadAsync(userId, channelId, lastReadMessageId, ct);

        // 2. Drop the cache key — best-effort. A stale non-zero badge is the safe
        //    failure direction; the next read or re-mark corrects it.
        if (_redisProvider.IsConnected)
        {
            try
            {
                var db = _redisProvider.Connection!.GetDatabase();
                await db.KeyDeleteAsync(UnreadKey(userId, channelId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unread: failed clearing cache key for {UserId}:{ChannelId} — read_states already updated",
                    userId,
                    channelId
                );
            }
        }

        // 3. Multi-device sync — best-effort.
        try
        {
            await _broadcaster.BroadcastUnreadCountAsync(
                userId,
                new UnreadCountPayload(channelId, guildId, 0),
                ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unread: zero-broadcast failed for {UserId}:{ChannelId} — continuing",
                userId,
                channelId
            );
        }
    }

    public async Task<IReadOnlyDictionary<long, int>> GetUnreadForUserAsync(
        long userId,
        IEnumerable<long> channelIds,
        CancellationToken ct = default
    )
    {
        var result = new Dictionary<long, int>();

        if (!_redisProvider.IsConnected)
        {
            _logger.LogDebug(
                "Unread: Redis unavailable — empty unread set for user {UserId}",
                userId
            );
            return result;
        }

        var ids = channelIds as IReadOnlyList<long> ?? channelIds.ToList();
        if (ids.Count == 0)
            return result;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var keys = ids.Select(cid => (RedisKey)UnreadKey(userId, cid)).ToArray();
            var values = await db.StringGetAsync(keys); // single MGET

            for (var i = 0; i < ids.Count; i++)
            {
                if (values[i].IsNullOrEmpty)
                    continue;
                if (values[i].TryParse(out long count) && count > 0)
                    result[ids[i]] = (int)count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unread: failed reading counts for user {UserId} — returning partial",
                userId
            );
        }

        return result;
    }

    private async Task<List<long>> ResolveRecipientIdsAsync(
        long? guildId,
        long channelId,
        long senderUserId
    )
    {
        // DM (no guild): the recipients are the channel's participants minus the sender.
        // DMs have no overrides, so no per-channel visibility check is needed.
        if (guildId is not { } gid)
        {
            var participantIds = await _dms.GetParticipantIdsAsync(channelId);
            return participantIds.Where(id => id != senderUserId).ToList();
        }

        var memberIds = await _guildRepository.GetMemberIdsAsync(gid);

        // Only members who can actually view the channel accrue unread for it — otherwise a
        // member would get a badge (and a non-zero /me/unread) for an override-hidden channel
        // like #staff.
        //
        // Resolved as ONE batched call rather than an await-per-member loop. The per-user cache
        // made each check cheap, but not free, and this runs for every message: at 54 members the
        // loop's stacked round-trips measured ~17ms, ~37% of the consumer's whole per-message
        // budget — and since dispatch is serial, that was a direct cap on messages/second.
        var candidates = memberIds.Where(id => id != senderUserId).ToList();
        return await _permissions.FilterByPermissionAsync(
            candidates,
            gid,
            Permission.ViewChannel,
            channelId
        );
    }

    public static string UnreadKey(long userId, long channelId) => $"unread:{userId}:{channelId}";
}
