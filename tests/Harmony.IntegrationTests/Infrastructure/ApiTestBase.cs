using System.Data.Common;
using System.Net.Http.Json;
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

        // 2. Flush Redis so unread:* / dedup:* keys never leak across tests.
        //    Safe because the suite is non-parallel (DisableTestParallelization).
        await FlushRedisAsync();

        // 3. Reset database state cleanly
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

                    // EnsureCreated() builds from the EF model, which deliberately leaves
                    // MessagesSearch.content_search UNMAPPED (it's a GENERATED tsvector column
                    // created via raw SQL in InitialSchema — see the entity + MessageSearchRepository).
                    // So the EnsureCreated test DB otherwise lacks that column and its GIN index, and
                    // every full-text search 500s (undefined_column). Re-apply the exact InitialSchema
                    // DDL here, idempotently, so the test schema matches the migrated dev/prod schema.
                    db.Database.ExecuteSqlRaw(
                        @"ALTER TABLE ""MessagesSearch""
                          ADD COLUMN IF NOT EXISTS content_search tsvector
                          GENERATED ALWAYS AS (to_tsvector('english', content)) STORED;"
                    );
                    db.Database.ExecuteSqlRaw(
                        @"CREATE INDEX IF NOT EXISTS ix_messages_search_content_search
                          ON ""MessagesSearch"" USING GIN (content_search);"
                    );

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

    private async Task FlushRedisAsync()
    {
        try
        {
            var provider =
                Factory.Services.GetRequiredService<Harmony.Infrastructure.Redis.IRedisConnectionProvider>();

            if (!provider.IsConnected)
                return;

            var endpoints = provider.Connection!.GetEndPoints();
            foreach (var endpoint in endpoints)
            {
                var server = provider.Connection.GetServer(endpoint);
                await server.FlushDatabaseAsync();
            }
        }
        catch
        {
            // Fail silently if Redis is unavailable — tests that need it will fail
            // on their own assertions, and fail-open paths are unaffected.
        }
    }

    /// <summary>
    /// Mints a guild-level invite code via the API using the current Authorization header
    /// (must be a member with CreateInvite — the owner always qualifies). Replaces the old
    /// per-guild permanent invite_code that tests used to read off the create response.
    /// </summary>
    protected async Task<string> CreateInviteCodeAsync(long guildId)
    {
        var resp = await Client.PostAsJsonAsync($"/api/guilds/{guildId}/invites", new { });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<InviteCodeResponse>();
        return body!.Code;
    }

    private record InviteCodeResponse(string Code);
}
