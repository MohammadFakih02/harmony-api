using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Infrastructure.Redis;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Computes effective permission bits using Discord's resolution model and caches the
/// result in Redis.
///
/// Resolution order:
///   1. Not a member            → 0 (no permissions).
///   2. Guild owner             → all permissions (hard bypass).
///   3. Base = @everyone bits, OR every explicitly-assigned role's bits.
///   4. Administrator bit set   → all permissions (bypass; overrides ignored).
///   5. Channel overrides (when a channelId is given), applied as (perms &amp; ~deny) | allow in order:
///      @everyone → aggregated assigned-role overrides → member-specific override.
///
/// Cache: a Redis HASH per (user, guild) — key <c>perms:{userId}:{guildId}</c>, field = channelId
/// ("0" for guild-level), value = bitmask — with a 30s TTL on the key. Per-member invalidation is a
/// single DEL; guild-wide invalidation DELs each member's key. Fails OPEN: if Redis is unavailable,
/// permissions are recomputed every call (correctness preserved, just uncached).
///
/// Note: member timeouts (CommunicationDisabledUntil) are intentionally NOT applied here — they are
/// time-sensitive and belong to the enforcement layer, so cached bits stay valid for the full TTL.
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Every defined permission bit OR'd together — the result for owners/administrators.</summary>
    private static readonly long AllPermissions =
        Enum.GetValues<Permission>().Aggregate(0L, (acc, p) => acc | (long)p);

    private readonly IGuildRepository _guilds;
    private readonly IRoleRepository _roles;
    private readonly IChannelPermissionOverrideRepository _overrides;
    private readonly IRedisConnectionProvider _redisProvider;
    private readonly ILogger<PermissionService> _logger;

    public PermissionService(
        IGuildRepository guilds,
        IRoleRepository roles,
        IChannelPermissionOverrideRepository overrides,
        IRedisConnectionProvider redisProvider,
        ILogger<PermissionService> logger
    )
    {
        _guilds = guilds;
        _roles = roles;
        _overrides = overrides;
        _redisProvider = redisProvider;
        _logger = logger;
    }

    public async Task<long> ResolveAsync(
        long userId,
        long guildId,
        long? channelId = null,
        CancellationToken ct = default
    )
    {
        var field = channelId?.ToString() ?? "0";

        if (await TryGetCachedAsync(userId, guildId, field) is { } cached)
            return cached;

        var resolved = await ComputeAsync(userId, guildId, channelId);

        await SetCachedAsync(userId, guildId, field, resolved);
        return resolved;
    }

    public async Task<bool> HasAsync(
        long userId,
        long guildId,
        Permission permission,
        long? channelId = null,
        CancellationToken ct = default
    )
    {
        var bits = await ResolveAsync(userId, guildId, channelId, ct);
        return (bits & (long)permission) == (long)permission;
    }

    /// <inheritdoc />
    public async Task<List<long>> FilterByPermissionAsync(
        IReadOnlyList<long> userIds,
        long guildId,
        Permission permission,
        long? channelId = null,
        CancellationToken ct = default
    )
    {
        if (userIds.Count == 0)
            return [];

        var field = channelId?.ToString() ?? "0";
        var bit = (long)permission;
        var granted = new HashSet<long>();

        // One round-trip for the whole group. Each user's entry is its own Redis hash, so this is a
        // pipelined batch rather than an HMGET — same effect: the per-user latency stops stacking.
        var cached = await TryGetCachedManyAsync(userIds, guildId, field);

        var misses = new List<long>();
        foreach (var id in userIds)
        {
            if (cached.TryGetValue(id, out var bits))
            {
                if ((bits & bit) == bit)
                    granted.Add(id);
            }
            else
            {
                misses.Add(id);
            }
        }

        // Misses resolve ONE AT A TIME, on purpose. A miss falls through to ComputeAsync, which
        // reads roles/overrides from Postgres via the scoped DbContext — and a DbContext permits
        // exactly one operation at a time, so resolving misses concurrently would throw "A second
        // operation was started on this context instance". The batch above already removes the cost
        // that mattered: in the steady state every entry is warm and this loop does nothing.
        foreach (var id in misses)
        {
            if (await HasAsync(id, guildId, permission, channelId, ct))
                granted.Add(id);
        }

        // Preserve the caller's ordering — callers pass member lists that are already ordered.
        return userIds.Where(granted.Contains).ToList();
    }

    /// <summary>
    /// Pipelined cache read for many users at once. Returns only the hits; a user absent from the
    /// result is a miss the caller must resolve itself. Fails open to "everything missed" so a
    /// Redis hiccup degrades to the slow path rather than to a wrong answer.
    /// </summary>
    private async Task<Dictionary<long, long>> TryGetCachedManyAsync(
        IReadOnlyList<long> userIds,
        long guildId,
        string field
    )
    {
        var hits = new Dictionary<long, long>(userIds.Count);
        if (!_redisProvider.IsConnected)
            return hits;

        try
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var batch = db.CreateBatch();
            var pending = userIds
                .Select(id => (id, task: batch.HashGetAsync(CacheKey(id, guildId), field)))
                .ToList();
            batch.Execute(); // flush every HGET together
            await Task.WhenAll(pending.Select(p => p.task));

            foreach (var (id, task) in pending)
            {
                var value = task.Result;
                if (value.HasValue && long.TryParse((string?)value, out var bits))
                    hits[id] = bits;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Permission cache batch read failed — resolving per user");
            return [];
        }

        return hits;
    }

    public Task InvalidateUserAsync(long userId, long guildId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return Task.CompletedTask;

        return SafeAsync(async () =>
            await _redisProvider.Connection!.GetDatabase().KeyDeleteAsync(CacheKey(userId, guildId))
        );
    }

    public async Task InvalidateGuildAsync(long guildId, CancellationToken ct = default)
    {
        if (!_redisProvider.IsConnected)
            return;

        var memberIds = await _guilds.GetMemberIdsAsync(guildId);
        if (memberIds.Count == 0)
            return;

        await SafeAsync(async () =>
        {
            var db = _redisProvider.Connection!.GetDatabase();
            var keys = memberIds.Select(uid => (RedisKey)CacheKey(uid, guildId)).ToArray();
            await db.KeyDeleteAsync(keys);
        });
    }

    // -------------------------------------------------------------------------
    // Resolution
    // -------------------------------------------------------------------------

    private async Task<long> ComputeAsync(long userId, long guildId, long? channelId)
    {
        var member = await _guilds.GetMemberAsync(guildId, userId);
        if (member is null)
            return 0; // not a member → no permissions

        if (member.IsOwner)
            return AllPermissions; // owner bypasses everything

        var everyone = await _roles.GetDefaultRoleAsync(guildId);
        var perms = everyone?.PermissionBits ?? 0;

        var memberRoles = await _roles.GetMemberRolesAsync(guildId, userId);
        foreach (var role in memberRoles)
            perms |= role.PermissionBits;

        if ((perms & (long)Permission.Administrator) != 0)
            return AllPermissions; // Administrator bypasses overrides too

        if (channelId is not { } cid)
            return perms;

        return ApplyChannelOverrides(
            perms,
            await _overrides.GetByChannelAsync(cid),
            everyoneRoleId: everyone?.Id,
            memberRoleIds: memberRoles.Select(r => r.Id).ToHashSet(),
            userId
        );
    }

    /// <summary>
    /// Applies channel overrides as <c>(perms &amp; ~deny) | allow</c> in Discord's precedence order:
    /// @everyone, then aggregated assigned-role overrides, then the member-specific override.
    /// </summary>
    private static long ApplyChannelOverrides(
        long perms,
        IReadOnlyList<Domain.Domain.Entities.ChannelPermissionOverride> overrides,
        long? everyoneRoleId,
        HashSet<long> memberRoleIds,
        long userId
    )
    {
        // 1. @everyone role override
        if (everyoneRoleId is { } everyoneId)
        {
            var everyoneOverride = overrides.FirstOrDefault(o =>
                o.TargetType == "role" && o.TargetId == everyoneId
            );
            if (everyoneOverride is not null)
                perms = (perms & ~everyoneOverride.DenyBits) | everyoneOverride.AllowBits;
        }

        // 2. Aggregated overrides for the member's assigned roles (deny then allow, combined)
        long rolesAllow = 0;
        long rolesDeny = 0;
        foreach (var o in overrides)
        {
            if (o.TargetType == "role" && o.TargetId != everyoneRoleId && memberRoleIds.Contains(o.TargetId))
            {
                rolesAllow |= o.AllowBits;
                rolesDeny |= o.DenyBits;
            }
        }
        perms = (perms & ~rolesDeny) | rolesAllow;

        // 3. Member-specific override (highest precedence)
        var memberOverride = overrides.FirstOrDefault(o =>
            o.TargetType == "user" && o.TargetId == userId
        );
        if (memberOverride is not null)
            perms = (perms & ~memberOverride.DenyBits) | memberOverride.AllowBits;

        return perms;
    }

    // -------------------------------------------------------------------------
    // Cache
    // -------------------------------------------------------------------------

    private async Task<long?> TryGetCachedAsync(long userId, long guildId, string field)
    {
        if (!_redisProvider.IsConnected)
            return null;

        try
        {
            var value = await _redisProvider
                .Connection!.GetDatabase()
                .HashGetAsync(CacheKey(userId, guildId), field);

            if (value.HasValue && long.TryParse((string?)value, out var bits))
                return bits;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Permission cache read failed — recomputing");
        }

        return null;
    }

    private async Task SetCachedAsync(long userId, long guildId, string field, long bits)
    {
        if (!_redisProvider.IsConnected)
            return;

        await SafeAsync(async () =>
        {
            var key = CacheKey(userId, guildId);
            var db = _redisProvider.Connection!.GetDatabase();
            await db.HashSetAsync(key, field, bits);
            // Refresh the TTL on every write so an actively-used (user,guild) entry stays warm.
            await db.KeyExpireAsync(key, CacheTtl);
        });
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Permission cache operation failed — ignoring (fail-open)");
        }
    }

    public static string CacheKey(long userId, long guildId) => $"perms:{userId}:{guildId}";
}
