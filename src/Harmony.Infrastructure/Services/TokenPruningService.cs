using System;
using System.Threading;
using System.Threading.Tasks;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Daily background sweep that deletes expired refresh tokens (and revoked ones older than 30 days) plus
/// expired trusted-device records, keeping the auth tables from growing unbounded. Failures are logged and
/// retried on the next cycle.
/// </summary>
public class TokenPruningService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TokenPruningService> _logger;
    private readonly TimeSpan _pruneInterval = TimeSpan.FromDays(1); // Execution interval: Once daily

    public TokenPruningService(
        IServiceScopeFactory scopeFactory,
        ILogger<TokenPruningService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TokenPruningService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting background refresh token pruning cycle.");

                using (var scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();

                    // Clean up revoked tokens after 30 days.
                    var revokedCutoff = DateTimeOffset.UtcNow.AddDays(-30);

                    // Explicitly name cancellationToken to prevent EF Core from mapping it as a query parameter
                    var deletedCount = await db.Database.ExecuteSqlRawAsync(
                        "DELETE FROM \"RefreshTokens\" WHERE \"expires_at\" < {0} OR (\"revoked_at\" IS NOT NULL AND \"revoked_at\" < {1})",
                        [DateTimeOffset.UtcNow, revokedCutoff],
                        cancellationToken: stoppingToken
                    );

                    _logger.LogInformation(
                        "Successfully pruned {Count} expired or historically revoked refresh tokens.",
                        deletedCount
                    );

                    var deletedDevices = await db.Database.ExecuteSqlRawAsync(
                        "DELETE FROM \"TrustedDevices\" WHERE \"expires_at\" < {0}",
                        [DateTimeOffset.UtcNow],
                        cancellationToken: stoppingToken
                    );

                    _logger.LogInformation(
                        "Successfully pruned {Count} expired trusted devices.",
                        deletedDevices
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the refresh token pruning cycle.");
            }

            // Wait until the next execution window or exit if stopping is requested
            await Task.Delay(_pruneInterval, stoppingToken);
        }

        _logger.LogInformation("TokenPruningService background worker stopped.");
    }
}
