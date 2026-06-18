using Harmony.Application.Interfaces.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Ensures the object-storage bucket exists on startup (mirrors <c>KeyspaceInitializer</c> for
/// Scylla). Best-effort: a missing/unreachable store is logged but does not crash the host, so it
/// can never take down unrelated boots or integration tests — the storage calls then fail
/// per-request with a clear error instead.
/// </summary>
public sealed class ObjectStorageBucketInitializer : IHostedService
{
    private readonly IFileStorageService _storage;
    private readonly ILogger<ObjectStorageBucketInitializer> _logger;

    public ObjectStorageBucketInitializer(
        IFileStorageService storage,
        ILogger<ObjectStorageBucketInitializer> logger
    )
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _storage.EnsureBucketAsync(cancellationToken);
            _logger.LogInformation("Object-storage bucket initialization complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Object-storage bucket initialization failed — uploads will fail until the store is reachable.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
