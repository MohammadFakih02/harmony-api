using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Periodically removes orphaned file attachments (hourly). A presign inserts a pending
/// (is_confirmed = false) row + mints a 5-minute PUT URL; if the client never uploads or
/// confirms, that row — and any object it left in the store — lingers forever. This sweep
/// deletes pending rows older than a grace window well past the presign TTL, best-effort
/// removing the object first.
///
/// The sweep body is exposed as <see cref="RunOnceAsync"/> so it can be invoked directly in
/// tests. Mirrors TokenPruningService / MuteExpiryService's scope-per-cycle pattern (the
/// repository is scoped; this service is a singleton).
/// </summary>
public class OrphanFileSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanFileSweepService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    // Grace window well past the 5-minute presign PUT TTL, so a slow-but-legitimate
    // upload-then-confirm in flight is never raced into deletion.
    private readonly TimeSpan _orphanAge = TimeSpan.FromMinutes(15);

    public OrphanFileSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<OrphanFileSweepService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrphanFileSweepService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the orphan-attachment sweep.");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("OrphanFileSweepService background worker stopped.");
    }

    /// <summary>
    /// Runs a single sweep: best-effort deletes each orphan's object, then removes the rows.
    /// Returns the number of rows swept. Safe to call directly (tests).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileAttachmentRepository>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();

        var cutoff = DateTimeOffset.UtcNow.Subtract(_orphanAge).ToUnixTimeMilliseconds();
        var orphans = await files.GetUnconfirmedOlderThanAsync(cutoff);
        if (orphans.Count == 0)
            return 0;

        foreach (var orphan in orphans)
        {
            // Object delete is best-effort: a failure (or a never-uploaded key) must not stop
            // us from reclaiming the DB row. A leftover object is caught by a store lifecycle
            // rule; a leftover row would never be retried.
            try
            {
                await storage.DeleteObjectAsync(orphan.MinioKey, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "OrphanFileSweep: failed to delete object {Key} — removing the row anyway",
                    orphan.MinioKey
                );
            }
        }

        files.RemoveRange(orphans);
        await files.SaveChangesAsync();

        _logger.LogInformation(
            "OrphanFileSweepService swept {Count} unconfirmed attachment(s).",
            orphans.Count
        );
        return orphans.Count;
    }
}
