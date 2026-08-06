using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Periodically (every 60s) reverts expired presence statuses back to online and
/// clears expired custom status messages. A user can set, say, Do-Not-Disturb "for
/// 4 hours" or a custom message that "clears after 1 hour"; this sweep is what makes
/// those timers actually fire.
///
/// For a reverted preferred status it calls <see cref="IPresenceService.SetPreferredStatusAsync"/>
/// so the Redis cache + effective status are recomputed and a StatusChanged broadcast
/// goes out (no-op for an offline user). Mirrors <see cref="MuteExpiryService"/>'s
/// scope-per-cycle pattern; the sweep body is exposed as <see cref="RunOnceAsync"/>
/// for direct invocation in tests.
/// </summary>
public class StatusExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StatusExpiryService> _logger;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(60);

    public StatusExpiryService(
        IServiceScopeFactory scopeFactory,
        ILogger<StatusExpiryService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StatusExpiryService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the status expiry sweep.");
            }

            await Task.Delay(_sweepInterval, stoppingToken);
        }

        _logger.LogInformation("StatusExpiryService background worker stopped.");
    }

    /// <summary>
    /// Runs a single sweep: reverts every expired preferred status to online, clears
    /// every expired custom status, persists, and re-broadcasts the reverted statuses.
    /// Returns the number of users touched. Safe to call directly (tests).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var presence = scope.ServiceProvider.GetRequiredService<IPresenceService>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expired = await users.GetUsersWithExpiredStatusAsync(now);
        if (expired.Count == 0)
            return 0;

        var reverted = new List<long>();
        var clearedMessages = new List<long>();
        foreach (var user in expired)
        {
            if (user.PreferredStatusExpiresAt is not null && user.PreferredStatusExpiresAt <= now)
            {
                user.PreferredStatus = PresenceStatus.Online;
                user.PreferredStatusExpiresAt = null;
                reverted.Add(user.Id);
            }

            if (user.StatusMessageExpiresAt is not null && user.StatusMessageExpiresAt <= now)
            {
                user.StatusMessage = null;
                user.StatusMessageExpiresAt = null;
                clearedMessages.Add(user.Id);
            }
        }

        await users.SaveChangesAsync();

        // Re-broadcast reverted statuses so connected tabs/friends see the change live.
        // One bad push must never abort the rest (same fail-open posture as MuteExpiry).
        foreach (var userId in reverted)
        {
            try
            {
                await presence.SetPreferredStatusAsync(userId, PresenceStatus.Online, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to broadcast reverted status for user {UserId}",
                    userId
                );
            }
        }

        // Clearing the DB column alone leaves the Redis cache (user:{id}:statusmsg) holding the stale
        // message, with no live StatusChanged — so observers keep seeing an expired status forever.
        // SetCustomStatusAsync(null) clears that cache key AND broadcasts the cleared message.
        foreach (var userId in clearedMessages)
        {
            try
            {
                await presence.SetCustomStatusAsync(userId, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to clear expired status message for user {UserId}",
                    userId
                );
            }
        }

        _logger.LogInformation(
            "StatusExpiryService swept {Count} user(s) ({Reverted} status revert(s), {Cleared} message clear(s)).",
            expired.Count,
            reverted.Count,
            clearedMessages.Count
        );

        return expired.Count;
    }
}
