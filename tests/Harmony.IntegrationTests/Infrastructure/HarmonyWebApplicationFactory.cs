using Harmony.Application.Interfaces.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harmony.IntegrationTests.Infrastructure;

public class HarmonyWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureTestServices(services =>
        {
            // The dummy LiveKit host is unreachable — hard voice moderation must be a no-op in
            // tests, not an HTTP call that waits out a timeout (it is fail-open in prod anyway).
            services.RemoveAll<ILiveKitRoomService>();
            services.AddSingleton<ILiveKitRoomService, NoOpLiveKitRoomService>();
        });
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
                        // Dummy LiveKit keys so ILiveKitTokenService.IsConfigured is true and the
                        // voice token endpoint mints a (dummy-signed) JWT — no real LiveKit call is
                        // made in tests; the token is only inspected, never used to connect.
                        ["LiveKit:ApiKey"] = "test-livekit-key",
                        ["LiveKit:ApiSecret"] = "test-livekit-secret-at-least-32-chars-long",
                        ["LiveKit:Host"] = "wss://test.livekit.cloud",
                    }
                );
            }
        );
    }

    private sealed class NoOpLiveKitRoomService : ILiveKitRoomService
    {
        public Task SetMicrophoneMutedAsync(
            long channelId,
            long userId,
            bool muted,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task SetCanSubscribeAsync(
            long channelId,
            long userId,
            bool canSubscribe,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task RemoveParticipantAsync(
            long channelId,
            long userId,
            CancellationToken ct = default
        ) => Task.CompletedTask;
    }
}
