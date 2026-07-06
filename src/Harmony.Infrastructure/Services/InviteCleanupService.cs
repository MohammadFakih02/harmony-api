using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Hourly sweep deleting dead guild invites — expired or exhausted (§17). Redeem/preview already
/// 410 dead codes, so this is pure housekeeping: without it the rows (and the expired entries in
/// the Invite People list) accumulate forever. Sweep body exposed as <see cref="RunOnceAsync"/>
/// for tests; scope-per-cycle like the other sweeps.
/// </summary>
public class InviteCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InviteCleanupService> _logger;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromHours(1);

    public InviteCleanupService(IServiceScopeFactory scopeFactory, ILogger<InviteCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InviteCleanupService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the invite cleanup sweep.");
            }

            await Task.Delay(_sweepInterval, stoppingToken);
        }

        _logger.LogInformation("InviteCleanupService background worker stopped.");
    }

    /// <summary>Runs a single sweep; returns the number of invites removed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var invites = scope.ServiceProvider.GetRequiredService<IGuildInviteRepository>();

        var removed = await invites.DeleteDeadAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (removed > 0)
            _logger.LogInformation("InviteCleanupService removed {Count} dead invite(s).", removed);
        return removed;
    }
}
