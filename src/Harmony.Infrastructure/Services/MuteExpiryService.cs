using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Periodically sweeps expired user mutes (every 60s). For each mute whose
/// MutedUntil has passed it deletes the row and notifies the owner with MuteExpired
/// so their connected tabs drop it from local state.
///
/// The sweep body is exposed as <see cref="RunOnceAsync"/> so it can be invoked
/// directly in tests without waiting on the timer. Mirrors TokenPruningService's
/// scope-per-cycle pattern (the repository is scoped; this service is a singleton).
/// </summary>
public class MuteExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<MuteExpiryService> _logger;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(60);

    public MuteExpiryService(
        IServiceScopeFactory scopeFactory,
        IHubBroadcaster broadcaster,
        ILogger<MuteExpiryService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MuteExpiryService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the mute expiry sweep.");
            }

            await Task.Delay(_sweepInterval, stoppingToken);
        }

        _logger.LogInformation("MuteExpiryService background worker stopped.");
    }

    /// <summary>
    /// Runs a single sweep: deletes every expired mute and broadcasts MuteExpired to
    /// each owner. Returns the number of mutes swept. Safe to call directly (tests).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var mutes = scope.ServiceProvider.GetRequiredService<IUserMuteRepository>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expired = await mutes.DeleteExpiredAsync(now);

        foreach (var mute in expired)
        {
            // One bad push must never abort the rest of the fan-out (same fail-open
            // posture as the presence/unread broadcasters).
            try
            {
                await _broadcaster.BroadcastMuteExpiredAsync(
                    mute.UserId,
                    new MuteExpiredPayload(mute.TargetId, mute.TargetType),
                    ct
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to broadcast MuteExpired to user {UserId} for {TargetType}:{TargetId}",
                    mute.UserId,
                    mute.TargetType,
                    mute.TargetId
                );
            }
        }

        if (expired.Count > 0)
            _logger.LogInformation("MuteExpiryService swept {Count} expired mute(s).", expired.Count);

        return expired.Count;
    }
}
