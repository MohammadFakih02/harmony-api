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
    private readonly IReadStateRepository _readStateRepository;
    private readonly IHubBroadcaster _broadcaster;
    private readonly IPermissionService _permissions;
    private readonly IDirectMessageRepository _dms;
    private readonly ILogger<RedisUnreadCountService> _logger;

    public RedisUnreadCountService(
        IRedisConnectionProvider redisProvider,
        IReadStateRepository readStateRepository,
        IHubBroadcaster broadcaster,
        IPermissionService permissions,
        IDirectMessageRepository dms,
        ILogger<RedisUnreadCountService> logger
    )
    {
        _redisProvider = redisProvider;
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
        // DM (no guild): bounded fan-out (≤10 participants) — keep the exact per-participant
        // absolute-count path. The O(recipients) problem D3 removes is a large-guild-channel
        // problem; a DM fan-out is cheap and DMs have no guild group to broadcast to.
        if (guildId is null)
        {
            await IncrementDmAsync(channelId, senderUserId, ct);
            return;
        }

        // Guild channel (D3): O(1) write. One channel-level counter INCR (the durable total the
        // read path diffs against) plus ONE guild-group activity ping — no per-recipient resolve,
        // INCR, or broadcast. Unread is computed read-time as channel-count − the user's mark.
        var gid = guildId.Value;

        if (_redisProvider.IsConnected)
        {
            try
            {
                var db = _redisProvider.Connection!.GetDatabase();
                await db.StringIncrementAsync(ChannelCountKey(channelId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unread: channel-count INCR failed for channel {ChannelId} — badge may lag until next message",
                    channelId
                );
            }
        }

        // Live bump for everyone in the guild. The client +1's the channel unless it's the active
        // one or its own message. Ids only — no content/name; hidden-channel confidentiality on the
        // read path is enforced by the ViewChannel filter in GetUnreadForUserAsync. Best-effort.
        try
        {
            await _broadcaster.BroadcastChannelActivityAsync(
                new ChannelActivityPayload(channelId, gid, senderUserId),
                ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unread: channel-activity broadcast failed for channel {ChannelId} — continuing",
                channelId
            );
        }
    }

    /// <summary>
    /// The unchanged DM path: resolve the ≤10 participants (minus the sender), pipeline one INCR of
    /// <c>unread:{userId}:{channelId}</c> each, then push the absolute count to each. Bounded, so
    /// the per-recipient shape is fine — only guild channels moved to the D3 read-time model.
    /// </summary>
    private async Task IncrementDmAsync(long channelId, long senderUserId, CancellationToken ct)
    {
        if (!_redisProvider.IsConnected)
        {
            _logger.LogDebug(
                "Unread: Redis unavailable — skipping DM increment for channel {ChannelId}",
                channelId
            );
            return;
        }

        var participantIds = await _dms.GetParticipantIdsAsync(channelId);
        var recipientIds = participantIds.Where(id => id != senderUserId).ToList();
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
                "Unread: pipelined DM INCR failed for channel {ChannelId} — skipping fan-out",
                channelId
            );
            return;
        }

        // Dispatched together, per-user try/catch inside each task so one dead recipient can't abort
        // the rest. IHubContext is built for concurrent use and the broadcaster holds no per-call state.
        await Task.WhenAll(
            pending.Select(async p =>
            {
                try
                {
                    await _broadcaster.BroadcastUnreadCountAsync(
                        p.userId,
                        new UnreadCountPayload(channelId, null, (int)p.incr.Result),
                        ct
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Unread: DM broadcast failed for user {UserId} on channel {ChannelId} — continuing",
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

        // 2. Clear the cache — best-effort. A stale non-zero badge is the safe failure direction;
        //    the next read or re-mark corrects it.
        if (_redisProvider.IsConnected)
        {
            try
            {
                var db = _redisProvider.Connection!.GetDatabase();
                if (guildId is null)
                {
                    // DM: the count lives per-user at unread:{user}:{channel} — drop it.
                    await db.KeyDeleteAsync(UnreadKey(userId, channelId));
                }
                else
                {
                    // Guild channel (D3): anchor the mark to the channel's current count, so
                    // read-time unread (count − mark) is zero right now. (long)Null == 0 for a
                    // brand-new channel with no counter yet.
                    var count = (long)await db.StringGetAsync(ChannelCountKey(channelId));
                    await db.StringSetAsync(MarkKey(userId, channelId), count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unread: failed clearing cache for {UserId}:{ChannelId} — read_states already updated",
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
        IReadOnlyDictionary<long, long> channelGuildMap,
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

        if (channelGuildMap.Count == 0)
            return result;

        // 1. ViewChannel filter (moved OFF the write path to here — per session, not per message).
        //    The channel counter now exists for every channel regardless of who can see it, so the
        //    read path must drop override-hidden channels (e.g. #staff): otherwise a member would
        //    get a count for them AND they'd inflate the guild-badge rollup. Cached, so cheap; one
        //    channel failing resolution is treated as not-viewable (fail-closed for that channel).
        var viewable = new List<(long channelId, long guildId)>();
        foreach (var kv in channelGuildMap)
        {
            try
            {
                if (await _permissions.HasAsync(userId, kv.Value, Permission.ViewChannel, kv.Key))
                    viewable.Add((kv.Key, kv.Value));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unread: ViewChannel resolve failed for {UserId} on channel {ChannelId} — treating as hidden",
                    userId,
                    kv.Key
                );
            }
        }
        if (viewable.Count == 0)
            return result;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var countKeys = viewable.Select(c => (RedisKey)ChannelCountKey(c.channelId)).ToArray();
            var markKeys = viewable.Select(c => (RedisKey)MarkKey(userId, c.channelId)).ToArray();
            var counts = await db.StringGetAsync(countKeys); // one MGET
            var marks = await db.StringGetAsync(markKeys); // one MGET

            // A channel with no mark is "caught up" (0) — this covers new members, post-deploy
            // existing members, and never-opened channels. Lazily anchor the mark to the current
            // count so the channel is correct from now on (this replaces an explicit guild-join
            // hook). Anchors are MSET together at the end.
            var anchors = new List<KeyValuePair<RedisKey, RedisValue>>();
            for (var i = 0; i < viewable.Count; i++)
            {
                var count = (long)counts[i]; // 0 when the counter key is absent
                if (marks[i].IsNullOrEmpty)
                {
                    anchors.Add(new((RedisKey)MarkKey(userId, viewable[i].channelId), count));
                    continue;
                }
                var unread = count - (long)marks[i];
                if (unread > 0)
                    result[viewable[i].channelId] = (int)unread;
            }

            if (anchors.Count > 0)
                await db.StringSetAsync(anchors.ToArray());
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

    // DM per-user count (unchanged path).
    public static string UnreadKey(long userId, long channelId) => $"unread:{userId}:{channelId}";

    // (D3) Monotonic total messages ever in a guild channel — the durable value the read path diffs.
    private static string ChannelCountKey(long channelId) => $"channel:{channelId}:count";

    // (D3) The channel-count snapshot at a user's last read — unread = channel-count − mark.
    private static string MarkKey(long userId, long channelId) => $"unread:mark:{userId}:{channelId}";
}
