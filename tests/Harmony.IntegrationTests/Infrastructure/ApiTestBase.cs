using System.Data.Common;
using Harmony.Infrastructure.Postgres;
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
    private static Respawner? _respawner;

    protected ApiTestBase()
    {
        Factory = new HarmonyWebApplicationFactory();
        Client = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
    }

    public virtual async Task InitializeAsync()
    {
        await ResetDatabaseAsync();
    }

    public virtual async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    private async Task ResetDatabaseAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();

        // 1. Ensure schema exists
        await db.Database.EnsureCreatedAsync();

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        // 2. Initialize Respawner if not already done
        if (_respawner == null)
        {
            var options = new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                TablesToIgnore = new[] { new Table("__EFMigrationsHistory") },
            };

            _respawner = await Respawner.CreateAsync(connection, options);
        }

        // 3. Reset the database to a clean state
        await _respawner.ResetAsync(connection);
    }
}
