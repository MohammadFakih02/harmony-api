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
                        // Silence Verbose SQL and Framework Logs
                        ["Logging:LogLevel:Default"] = "Warning",
                        ["Logging:LogLevel:Microsoft"] = "Warning",
                        ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] =
                            "Warning",
                        ["Logging:LogLevel:Harmony"] = "Warning",

                        ["ConnectionStrings:Postgres"] =
                            "Host=localhost;Port=5432;Database=harmony_test;Username=admin;Password=secret",
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
