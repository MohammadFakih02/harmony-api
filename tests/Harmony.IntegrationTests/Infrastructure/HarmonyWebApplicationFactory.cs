using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Harmony.IntegrationTests.Infrastructure;

public class HarmonyWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration(
            (context, config) =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Postgres"] =
                            "Host=localhost;Port=5432;Database=harmony_test;Username=admin;Password=secret",

                        // Empty string → DependencyInjection skips AddStackExchangeRedis.
                        // Tests run with the in-process backplane; no Redis instance required.
                        // This means all SignalR connections in a test share the same process,
                        // which is exactly what we want — no cross-process routing to worry about.
                        ["ConnectionStrings:Redis"] = "",

                        ["ConnectionStrings:RabbitMQ"] = "amqp://admin:secret@localhost:5672",
                        ["Jwt:Key"] = "test-super-secret-key-minimum-32-characters-long",
                        ["Jwt:Issuer"] = "harmony-api",
                        ["Jwt:Audience"] = "harmony-client",
                        ["Jwt:AccessTokenExpiryMinutes"] = "15",
                        ["Jwt:RefreshTokenExpiryDays"] = "7",
                        ["Snowflake:WorkerId"] = "0",
                        ["Snowflake:DatacenterId"] = "0",
                        ["ScyllaDB:ContactPoints:0"] = "127.0.0.1",
                        ["ScyllaDB:Port"] = "9042",
                        ["ScyllaDB:Keyspace"] = "harmony_test",
                    }
                );
            }
        );
    }
}
