using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// The D1 <see cref="IPresenceService"/> implementation used outside the Test environment: a thin
/// adapter that forwards each call to the per-user <see cref="IUserGrain"/> (keyed by userId). The
/// two bulk reads (the member-list hot path) fan out across the N grains with <c>Task.WhenAll</c> —
/// at this scale each is an in-process, sub-microsecond grain call, so N parallel reads replace the
/// single Redis MGET with no meaningful cost, and an offline user's grain returns "offline" without
/// touching Postgres (its durable status load is lazy).
///
/// <para>Fail-open is preserved here, not in the grain callers: a grain-call failure (silo down mid
/// call) degrades to the same defaults the Redis service returned — offline for reads, a no-op for
/// writes — and <see cref="IsConnectedAsync"/> fails <em>closed</em> (returns true) so the push gate
/// skips rather than risk buzzing a connected user. The Test environment keeps
/// <see cref="Harmony.Infrastructure.Redis.RedisPresenceService"/> (Orleans is not co-hosted there).</para>
/// </summary>
public sealed class GrainPresenceService : IPresenceService
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<GrainPresenceService> _logger;

    public GrainPresenceService(IGrainFactory grains, ILogger<GrainPresenceService> logger)
    {
        _grains = grains;
        _logger = logger;
    }

    private IUserGrain Grain(long userId) => _grains.GetGrain<IUserGrain>(userId);

    public Task SetOnlineAsync(long userId, string connectionId, CancellationToken ct = default) =>
        SafeAsync(userId, g => g.SetOnline(connectionId), nameof(SetOnlineAsync));

    public Task SetOfflineAsync(long userId, string connectionId, CancellationToken ct = default) =>
        SafeAsync(userId, g => g.SetOffline(connectionId), nameof(SetOfflineAsync));

    public Task HeartbeatAsync(long userId, string connectionId, CancellationToken ct = default) =>
        SafeAsync(userId, g => g.Heartbeat(connectionId), nameof(HeartbeatAsync));

    public Task SetIdleAsync(long userId, bool idle, CancellationToken ct = default) =>
        SafeAsync(userId, g => g.SetIdle(idle), nameof(SetIdleAsync));

    public Task SetCustomStatusAsync(long userId, string? message, CancellationToken ct = default) =>
        SafeAsync(userId, g => g.SetCustomStatus(message), nameof(SetCustomStatusAsync));

    public Task SetPreferredStatusAsync(long userId, string preferred, CancellationToken ct = default) =>
        SafeAsync(userId, g => g.SetPreferredStatus(preferred), nameof(SetPreferredStatusAsync));

    public async Task<string> GetStatusAsync(long userId, CancellationToken ct = default)
    {
        try
        {
            return await Grain(userId).GetStatus();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: GetStatus failed for user {UserId}", userId);
            return PresenceStatus.Offline;
        }
    }

    public async Task<string> GetPreferredStatusAsync(long userId, CancellationToken ct = default)
    {
        try
        {
            return await Grain(userId).GetPreferredStatus();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: GetPreferredStatus failed for user {UserId}", userId);
            return PresenceStatus.Online;
        }
    }

    public async Task<bool> IsConnectedAsync(long userId, CancellationToken ct = default)
    {
        // Fails CLOSED for the push gate: an uncertain state reads as "connected" so the caller
        // skips the push rather than risk buzzing a user who is looking at the app.
        try
        {
            return await Grain(userId).IsConnected();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: IsConnected failed for user {UserId} — assuming connected", userId);
            return true;
        }
    }

    public async Task<IReadOnlyDictionary<long, string>> GetStatusesAsync(
        IEnumerable<long> userIds,
        CancellationToken ct = default
    )
    {
        var ids = userIds.Distinct().ToList();
        var result = ids.ToDictionary(id => id, _ => PresenceStatus.Offline);
        if (ids.Count == 0)
            return result;

        try
        {
            var statuses = await Task.WhenAll(ids.Select(id => Grain(id).GetStatus()));
            for (var i = 0; i < ids.Count; i++)
                result[ids[i]] = statuses[i];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: bulk GetStatuses failed — returning offline defaults");
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<long, string?>> GetStatusMessagesAsync(
        IEnumerable<long> userIds,
        CancellationToken ct = default
    )
    {
        var ids = userIds.Distinct().ToList();
        var result = ids.ToDictionary(id => id, _ => (string?)null);
        if (ids.Count == 0)
            return result;

        try
        {
            var messages = await Task.WhenAll(ids.Select(id => Grain(id).GetStatusMessage()));
            for (var i = 0; i < ids.Count; i++)
                result[ids[i]] = messages[i];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: bulk GetStatusMessages failed — returning null defaults");
        }

        return result;
    }

    /// <summary>
    /// The global crash-recovery sweep is retired under D1: each <see cref="UserGrain"/> prunes its
    /// own stale connections on a grain timer, so there is nothing to scan globally. Kept on the
    /// interface (and as a no-op) only for the Redis implementation the Test environment still uses.
    /// </summary>
    public Task<int> SweepStaleAsync(TimeSpan staleThreshold, CancellationToken ct = default) =>
        Task.FromResult(0);

    /// <summary>Runs a grain write, swallowing failures so presence never breaks the hub lifecycle.</summary>
    private async Task SafeAsync(long userId, Func<IUserGrain, Task> call, string op)
    {
        try
        {
            await call(Grain(userId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Presence: {Op} failed for user {UserId} — continuing", op, userId);
        }
    }
}
