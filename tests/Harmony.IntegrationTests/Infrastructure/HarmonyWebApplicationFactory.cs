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

                        ["ASPNETCORE_ENVIRONMENT"] = "Test", // Explicit environment variable mapping
                        ["ConnectionStrings:Postgres"] =
                            "Host=localhost;Port=5432;Database=harmony_test;Username=admin;Password=secret",
                        // Real Redis — exercises dedup + unread-count paths end-to-end.
                        // The SignalR backplane stays OFF (DependencyInjection guards on
                        // `&& !isTest`), so this activates the data paths, not the backplane.
                        ["ConnectionStrings:Redis"] =
                            "localhost:6379,abortConnect=false,allowAdmin=true",
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
                        // Real MinIO (S3-compatible) — exercises presign → PUT → confirm end-to-end.
                        // Secret matches docker-compose's MINIO_ROOT_PASSWORD (and the CI service).
                        ["ObjectStorage:Endpoint"] = "localhost:9000",
                        ["ObjectStorage:AccessKey"] = "admin",
                        ["ObjectStorage:SecretKey"] = "secretpassword",
                        // Hyphen, not underscore: S3/MinIO bucket names reject underscores
                        // (unlike the Postgres/Scylla "harmony_test").
                        ["ObjectStorage:BucketName"] = "harmony-test",
                        ["ObjectStorage:UseSSL"] = "false",
                    }
                );
            }
        );
    }
}
