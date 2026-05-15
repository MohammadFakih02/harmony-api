using Harmony.Infrastructure.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore; // Added for Database.EnsureCreated
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.IntegrationTests.Infrastructure;

public abstract class ApiTestBase : IAsyncLifetime
{
    protected readonly HarmonyWebApplicationFactory Factory;
    protected readonly HttpClient Client;

    protected ApiTestBase()
    {
        Factory = new HarmonyWebApplicationFactory();
        Client = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
    }

    public async Task InitializeAsync()
    {
        await CleanDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        // Note: CleanDatabaseAsync is called at the start of tests.
        // You usually don't need to clean it again at disposal
        // unless you want to save space.
        await CleanDatabaseAsync();
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    private async Task CleanDatabaseAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarmonyDbContext>();

        // 1. Ensure the database schema exists
        // This will create the tables if they don't exist yet.
        await db.Database.EnsureCreatedAsync();

        // 2. Clear data
        // We use a try-catch or check for existing tables because
        // even after EnsureCreated, if there's no data, it's fine.
        // However, standard RemoveRange on a freshly created DB is now safe.

        db.RefreshTokens.RemoveRange(db.RefreshTokens);
        db.Users.RemoveRange(db.Users);

        await db.SaveChangesAsync();
    }
}
