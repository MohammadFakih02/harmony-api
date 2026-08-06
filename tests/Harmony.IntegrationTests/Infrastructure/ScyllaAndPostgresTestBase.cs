using Cassandra;
using Harmony.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;

namespace Harmony.IntegrationTests.Infrastructure;

public abstract class ScyllaAndPostgresTestBase : ScyllaTestBase
{
    private static readonly object PostgresLock = new();
    private static bool _postgresInitialized;
    private static Respawner? _respawner;
    protected HarmonyDbContext Db { get; private set; } = null!;

    private const string ConnectionString =
        "Host=localhost;Port=5432;Database=harmony_test;Username=admin;Password=secret";

    protected abstract IEnumerable<string> PostgresTablesToIgnore { get; }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var options = new DbContextOptionsBuilder<HarmonyDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        Db = new HarmonyDbContext(options);

        // Only run PostgreSQL schema creation check once for the entire test run
        if (!_postgresInitialized)
        {
            lock (PostgresLock)
            {
                if (!_postgresInitialized)
                {
                    Db.Database.EnsureCreated();
                    _postgresInitialized = true;
                }
            }
        }

        var connection = Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        if (_respawner is null)
        {
            _respawner = await Respawner.CreateAsync(
                connection,
                new RespawnerOptions
                {
                    DbAdapter = DbAdapter.Postgres,
                    TablesToIgnore = PostgresTablesToIgnore.Select(t => new Table(t)).ToArray(),
                }
            );
        }

        await _respawner.ResetAsync(connection);
    }

    public override async Task DisposeAsync()
    {
        var connection = Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        if (_respawner is not null)
            await _respawner.ResetAsync(connection);

        await Db.DisposeAsync();
        await base.DisposeAsync();
    }
}
