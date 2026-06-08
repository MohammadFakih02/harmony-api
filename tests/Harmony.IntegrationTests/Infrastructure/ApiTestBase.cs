using System.Data.Common;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.RabbitMQ; // Added for Topology names
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Respawn.Graph;

namespace Harmony.IntegrationTests.Infrastructure;

public abstract class ApiTestBase : IAsyncLifetime
{
    protected readonly HarmonyWebApplicationFactory Factory;
    protected readonly HttpClient Client;

    private static readonly object Lock = new();
    private static bool _dbInitialized;
    private static Respawner? _respawner;

    // Modified constructor to accept the class-bounded factory instance from xUnit
    protected ApiTestBase(HarmonyWebApplicationFactory factory)
    {
        Factory = factory;
        Client = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
    }

    public virtual async Task InitializeAsync()
    {
        // 1. Purge durable queues before each test to prevent cross-test message pollution
        await PurgeQueuesAsync();

        // 2. Reset database state cleanly
        await ResetDatabaseAsync();
    }

    public virtual Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask; // xUnit manages the Factory lifetime at the class boundary
    }

    private async Task PurgeQueuesAsync()
    {
        try
        {
            var rabbitConnection = Factory.Services.GetRequiredService<RabbitMQConnection>();
            await using var channel = await rabbitConnection.CreateChannelAsync();

            await channel.QueuePurgeAsync(Topology.ScyllaMessageQueue);
            await channel.QueuePurgeAsync(Topology.SearchIndexQueue);
            await channel.QueuePurgeAsync(Topology.NotificationQueue);
        }
        catch
        {
            // Fail silently if queues are not yet declared or connection is offline
        }
    }

    private async Task ResetDatabaseAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();

        if (!_dbInitialized)
        {
            lock (Lock)
            {
                if (!_dbInitialized)
                {
                    db.Database.EnsureCreated();
                    _dbInitialized = true;
                }
            }
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        if (_respawner == null)
        {
            var options = new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                TablesToIgnore = new[] { new Table("__EFMigrationsHistory") },
            };

            _respawner = await Respawner.CreateAsync(connection, options);
        }

        await _respawner.ResetAsync(connection);
    }
}
