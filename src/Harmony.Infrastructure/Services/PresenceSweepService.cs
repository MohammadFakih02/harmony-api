using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Crash-recovery sweep for presence. Every 30s it asks <see cref="IPresenceService"/> to
/// reap any user whose last heartbeat is older than 90s (two missed 45s client heartbeats) —
/// the ghost left when a client crashes or the server restarts before OnDisconnectedAsync can
/// run, so the graceful <see cref="IPresenceService.SetOfflineAsync"/> path never fired.
///
/// Without this the <c>presence:online</c> ZSET grows unbounded, and a returning user's
/// lingering session-set entry suppresses their next OnlineStatus broadcast (the
/// SetOnlineAsync "connectionCount != 1" guard sees a phantom second tab).
///
/// Mirrors <see cref="MuteExpiryService"/>/<see cref="StatusExpiryService"/>'s
/// scope-per-cycle timer; gated out of the Test environment in DI. The reap body lives in
/// RedisPresenceService (where the keys and fan-out helpers already are).
/// </summary>
public class PresenceSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PresenceSweepService> _logger;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);

    // Two missed 45s heartbeats. A single dropped heartbeat is tolerated; the 60-90s window
    // where the status dot has already gone grey (user:{id}:status TTL is 60s) but the session
    // ghost lingers is harmless.
    private readonly TimeSpan _staleThreshold = TimeSpan.FromSeconds(90);

    public PresenceSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<PresenceSweepService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PresenceSweepService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // IPresenceService is scoped (it injects scoped repos for the friend/guild
                // fan-out), so resolve a fresh scope each cycle — same pattern as the other sweeps.
                using var scope = _scopeFactory.CreateScope();
                var presence = scope.ServiceProvider.GetRequiredService<IPresenceService>();
                await presence.SweepStaleAsync(_staleThreshold, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the presence sweep.");
            }

            await Task.Delay(_sweepInterval, stoppingToken);
        }

        _logger.LogInformation("PresenceSweepService background worker stopped.");
    }
}
