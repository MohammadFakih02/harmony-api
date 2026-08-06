using System.Text.Json;
using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="IUserDisplayCache"/> — caches the sender username + avatar key the
/// ScyllaMessageConsumer stamps on every broadcast message, so the hot path doesn't hit Postgres
/// per message. Mirrors <see cref="RedisMessageDeduplicator"/>: it shares the single
/// <see cref="IRedisConnectionProvider"/> connection and FAILS OPEN — any miss, null connection,
/// or Redis error returns null (on get) or is swallowed (on set/invalidate), so the caller always
/// falls back to the user repository (the source of truth).
///
/// A 5-minute TTL backstops missed invalidations; the two mutation sites (username / avatar
/// change) evict explicitly, and the client also live-patches display via ProfileUpdated.
/// </summary>
public sealed class RedisUserDisplayCache : IUserDisplayCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IRedisConnectionProvider _redisProvider;
    private readonly ILogger<RedisUserDisplayCache> _logger;

    public RedisUserDisplayCache(
        IRedisConnectionProvider redisProvider,
        ILogger<RedisUserDisplayCache> logger
    )
    {
        _redisProvider = redisProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<UserDisplay?> GetAsync(long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return null;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var raw = await db.StringGetAsync(BuildKey(userId));
            if (raw.IsNullOrEmpty)
                return null;

            string json = raw!;
            var dto = JsonSerializer.Deserialize<CachedDisplay>(json);
            return dto is null ? null : new UserDisplay(dto.U, dto.A);
        }
        catch (Exception ex)
        {
            // Fail open: a cache read error must never block the broadcast — the caller
            // resolves the sender from Postgres instead.
            _logger.LogWarning(ex, "UserDisplayCache: get failed for {UserId} — failing open", userId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SetAsync(long userId, UserDisplay value, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var payload = JsonSerializer.Serialize(new CachedDisplay(value.Username, value.AvatarKey));
            await db.StringSetAsync(BuildKey(userId), payload, Ttl, When.Always, CommandFlags.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserDisplayCache: set failed for {UserId} — ignoring", userId);
        }
    }

    /// <inheritdoc/>
    public async Task InvalidateAsync(long userId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        try
        {
            await _redisProvider.Connection!.GetDatabase().KeyDeleteAsync(BuildKey(userId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserDisplayCache: invalidate failed for {UserId} — ignoring", userId);
        }
    }

    /// <summary>Builds the Redis key for a user's cached display fields. Format: <c>userdisplay:{userId}</c></summary>
    public static string BuildKey(long userId) => $"userdisplay:{userId}";

    // Compact on-wire shape (short property names keep the blob tiny).
    private sealed record CachedDisplay(string U, string? A);
}
