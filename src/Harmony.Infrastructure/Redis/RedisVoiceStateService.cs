using System.Text.Json;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="IVoiceStateService"/>. Ephemeral voice-room membership modeled on
/// <see cref="RedisPresenceService"/> — a HASH per room plus a per-user pointer to their current
/// room, so joining a new room evicts the old one (Discord's single-voice-session behavior).
///
/// Keys:
///   • <c>voice:channel:{channelId}</c> HASH — field = userId, value = the participant's state JSON.
///   • <c>voice:user:{userId}</c> STRING — the channelId of the room the user is currently in.
///   • <c>voice:users</c> SET — every userId currently in any room, so the sweep can enumerate them.
///
/// Ghost detection reuses presence: a voice member whose <c>user:{id}:status</c> key is absent has
/// gone offline (that key's 60s TTL lapses on a crash), so the sweep reaps them. Fails open
/// throughout — if Redis is unavailable, voice is simply untracked; never throws into the hub.
/// </summary>
public sealed class RedisVoiceStateService : IVoiceStateService
{
    private const string UsersSetKey = "voice:users";

    private readonly IRedisConnectionProvider _redisProvider;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<RedisVoiceStateService> _logger;

    public RedisVoiceStateService(
        IRedisConnectionProvider redisProvider,
        IHubBroadcaster broadcaster,
        ILogger<RedisVoiceStateService> logger
    )
    {
        _redisProvider = redisProvider;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task JoinAsync(
        long channelId,
        long? guildId,
        long userId,
        CancellationToken ct = default
    )
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();

            // Evict any prior room first (joining a new room leaves the old one). The prior room's
            // pointer still points at it here, so LeaveRoomAsync + the pointer delete are safe.
            var prev = await db.StringGetAsync(UserKey(userId));
            if (!prev.IsNullOrEmpty && long.TryParse(prev.ToString(), out var prevRoom))
            {
                if (prevRoom == channelId)
                {
                    // Rejoin of the same room (e.g. a reconnect): nothing to evict.
                }
                else
                {
                    await LeaveRoomAsync(db, userId, prevRoom, ct);
                    await db.KeyDeleteAsync(UserKey(userId));
                }
            }

            var state = new StoredState(
                guildId,
                IsMuted: false,
                IsDeafened: false,
                IsVideoOn: false,
                IsStreaming: false,
                JoinedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );

            await db.HashSetAsync(
                ChannelKey(channelId),
                userId.ToString(),
                JsonSerializer.Serialize(state)
            );
            await db.StringSetAsync(UserKey(userId), channelId.ToString());
            await db.SetAddAsync(UsersSetKey, userId.ToString());

            await SafeBroadcastAsync(() =>
                _broadcaster.BroadcastVoiceParticipantJoinedAsync(ToPayload(channelId, userId, state), ct)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: join failed for user {UserId} channel {ChannelId} — continuing", userId, channelId);
        }
    }

    public async Task LeaveAsync(long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var current = await db.StringGetAsync(UserKey(userId));
            if (current.IsNullOrEmpty || !long.TryParse(current.ToString(), out var channelId))
            {
                // Not in a room — still make sure the sweep-set doesn't keep a stray id.
                await db.SetRemoveAsync(UsersSetKey, userId.ToString());
                return;
            }

            await LeaveRoomAsync(db, userId, channelId, ct);
            await db.KeyDeleteAsync(UserKey(userId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: leave failed for user {UserId} — continuing", userId);
        }
    }

    public async Task UpdateStateAsync(
        long userId,
        bool isMuted,
        bool isDeafened,
        bool isVideoOn,
        bool isStreaming,
        CancellationToken ct = default
    )
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var current = await db.StringGetAsync(UserKey(userId));
            if (current.IsNullOrEmpty || !long.TryParse(current.ToString(), out var channelId))
                return;

            var raw = await db.HashGetAsync(ChannelKey(channelId), userId.ToString());
            if (raw.IsNullOrEmpty)
                return;

            var existing = Deserialize(raw!);
            if (existing is null)
                return;

            var updated = existing with
            {
                IsMuted = isMuted,
                IsDeafened = isDeafened,
                IsVideoOn = isVideoOn,
                IsStreaming = isStreaming,
            };

            await db.HashSetAsync(
                ChannelKey(channelId),
                userId.ToString(),
                JsonSerializer.Serialize(updated)
            );

            await SafeBroadcastAsync(() =>
                _broadcaster.BroadcastVoiceStateUpdatedAsync(ToPayload(channelId, userId, updated), ct)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: state update failed for user {UserId} — continuing", userId);
        }
    }

    public async Task<IReadOnlyList<VoiceParticipantPayload>> GetChannelParticipantsAsync(
        long channelId,
        CancellationToken ct = default
    )
    {
        if (!_redisProvider.IsConnected)
            return [];

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var entries = await db.HashGetAllAsync(ChannelKey(channelId));

            var result = new List<VoiceParticipantPayload>(entries.Length);
            foreach (var entry in entries)
            {
                if (!long.TryParse(entry.Name.ToString(), out var userId))
                    continue;
                var state = Deserialize(entry.Value!);
                if (state is not null)
                    result.Add(ToPayload(channelId, userId, state));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: participants read failed for channel {ChannelId}", channelId);
            return [];
        }
    }

    public async Task<int> SweepGhostsAsync(CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return 0;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var members = await db.SetMembersAsync(UsersSetKey);
            if (members.Length == 0)
                return 0;

            var reaped = 0;
            foreach (var member in members)
            {
                if (!long.TryParse(member.ToString(), out var userId))
                {
                    await db.SetRemoveAsync(UsersSetKey, member); // drop an unparseable ghost
                    continue;
                }

                // Presence is the connectivity authority: its status key (60s TTL, heartbeat-kept)
                // lapses when a client crashes, so its absence means the user is offline → a ghost.
                if (await db.KeyExistsAsync(RedisPresenceService.StatusKey(userId)))
                    continue;

                try
                {
                    await LeaveAsync(userId, ct);
                    reaped++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Voice: failed reaping ghost user {UserId} — continuing", userId);
                }
            }

            if (reaped > 0)
                _logger.LogInformation("Voice: swept {Count} ghost participant(s).", reaped);

            return reaped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: ghost sweep failed — continuing");
            return 0;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    /// <summary>
    /// Removes a user from a specific room's HASH + the global sweep-set and broadcasts the leave.
    /// Does NOT touch <c>voice:user:{userId}</c> — the caller owns the pointer (the evict path leaves
    /// it for JoinAsync to overwrite; the real-leave path deletes it after this returns).
    /// </summary>
    private async Task LeaveRoomAsync(IDatabase db, long userId, long channelId, CancellationToken ct)
    {
        var raw = await db.HashGetAsync(ChannelKey(channelId), userId.ToString());
        var guildId = raw.IsNullOrEmpty ? null : Deserialize(raw!)?.GuildId;

        await db.HashDeleteAsync(ChannelKey(channelId), userId.ToString());
        await db.SetRemoveAsync(UsersSetKey, userId.ToString());

        await SafeBroadcastAsync(() =>
            _broadcaster.BroadcastVoiceParticipantLeftAsync(
                new VoiceParticipantLeftPayload(channelId, guildId, userId),
                ct
            )
        );
    }

    private async Task SafeBroadcastAsync(Func<Task> broadcast)
    {
        try
        {
            await broadcast();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: broadcast failed — continuing");
        }
    }

    private static VoiceParticipantPayload ToPayload(long channelId, long userId, StoredState s) =>
        new(
            channelId,
            s.GuildId,
            userId,
            s.IsMuted,
            s.IsDeafened,
            s.IsVideoOn,
            s.IsStreaming,
            s.JoinedAt
        );

    private static StoredState? Deserialize(RedisValue raw)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredState>(raw.ToString());
        }
        catch
        {
            return null; // a malformed entry is treated as absent rather than poisoning a read
        }
    }

    private static string ChannelKey(long channelId) => $"voice:channel:{channelId}";

    private static string UserKey(long userId) => $"voice:user:{userId}";

    /// <summary>The per-participant state persisted in the room HASH.</summary>
    private sealed record StoredState(
        long? GuildId,
        bool IsMuted,
        bool IsDeafened,
        bool IsVideoOn,
        bool IsStreaming,
        long JoinedAt
    );
}
