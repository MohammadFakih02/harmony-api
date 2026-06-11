using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
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
    private readonly ILogger<RedisUnreadCountService> _logger;

    public RedisUnreadCountService(
        IRedisConnectionProvider redisProvider,
        IGuildRepository guildRepository,
        IReadStateRepository readStateRepository,
        IHubBroadcaster broadcaster,
        ILogger<RedisUnreadCountService> logger
    )
    {
        _redisProvider = redisProvider;
        _guildRepository = guildRepository;
        _readStateRepository = readStateRepository;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task IncrementForChannelAsync(
        long guildId,
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

        // Recipient-resolution seam: guild members today; DMs branch here later.
        var recipientIds = await ResolveRecipientIdsAsync(guildId, senderUserId);
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

        foreach (var (uid, incrTask) in pending)
        {
            try
            {
                await _broadcaster.BroadcastUnreadCountAsync(
                    uid,
                    new UnreadCountPayload(channelId, guildId, (int)incrTask.Result),
                    ct
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unread: broadcast failed for user {UserId} on channel {ChannelId} — continuing",
                    uid,
                    channelId
                );
            }
        }
    }

    public async Task MarkReadAsync(
        long userId,
        long guildId,
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

    private async Task<List<long>> ResolveRecipientIdsAsync(long guildId, long senderUserId)
    {
        var memberIds = await _guildRepository.GetMemberIdsAsync(guildId);
        return memberIds.Where(id => id != senderUserId).ToList();
    }

    public static string UnreadKey(long userId, long channelId) => $"unread:{userId}:{channelId}";
}
