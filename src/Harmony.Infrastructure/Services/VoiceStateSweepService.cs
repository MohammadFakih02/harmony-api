using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Ghost-recovery sweep for voice state. Every 30s it asks <see cref="IVoiceStateService"/> to reap
/// any participant whose presence has gone offline — the ghost left when a client crashes or the
/// server restarts before the hub's OnDisconnectedAsync (which leaves voice on last disconnect) can
/// run. The reap body lives in RedisVoiceStateService (where the keys + fan-out are), keyed off the
/// same presence status-key signal that <see cref="PresenceSweepService"/> uses.
///
/// Mirrors PresenceSweepService's scope-per-cycle timer; gated out of the Test environment in DI.
/// </summary>
public class VoiceStateSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceStateSweepService> _logger;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);

    public VoiceStateSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<VoiceStateSweepService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VoiceStateSweepService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // IVoiceStateService is scoped (it broadcasts through the scoped IHubBroadcaster),
                // so resolve a fresh scope each cycle — same pattern as the other sweeps.
                using var scope = _scopeFactory.CreateScope();
                var voice = scope.ServiceProvider.GetRequiredService<IVoiceStateService>();
                await voice.SweepGhostsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the voice-state sweep.");
            }

            await Task.Delay(_sweepInterval, stoppingToken);
        }

        _logger.LogInformation("VoiceStateSweepService background worker stopped.");
    }
}
