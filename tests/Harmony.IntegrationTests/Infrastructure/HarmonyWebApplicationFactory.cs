using System.Collections.Concurrent;
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

            // No real SMTP in tests — capture every send in-memory so a test can pull the
            // verification/2FA/reset link or code straight out of the "sent" mail instead of
            // needing Mailpit or a sleep-and-poll.
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<CapturingEmailSender>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<CapturingEmailSender>());

            // No real Google verification in tests — a test "registers" a fake ID token string
            // mapped to whatever GoogleUserInfo it wants VerifyAsync to return, instead of needing
            // a real Google-signed JWT.
            services.RemoveAll<IGoogleTokenVerifier>();
            services.AddSingleton<FakeGoogleTokenVerifier>();
            services.AddSingleton<IGoogleTokenVerifier>(sp =>
                sp.GetRequiredService<FakeGoogleTokenVerifier>()
            );
        });
        builder.ConfigureAppConfiguration(
            (context, config) =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        // Log verbosity for Test is set in code (Program.cs's UseSerilog callback
                        // branches on IsEnvironment("Test") -> MinimumLevel.Warning()), not here —
                        // Serilog ignores the Microsoft.Extensions.Logging "Logging:LogLevel" keys
                        // these used to be.
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

    /// <summary>In-memory <see cref="IEmailSender"/> fake for integration tests — records every
    /// send instead of talking to SMTP so a test can grep the captured HTML/text for a link or
    /// code. Singleton so every request in a test resolves the same queue.</summary>
    public sealed class CapturingEmailSender : IEmailSender
    {
        public ConcurrentQueue<CapturedEmail> Sent { get; } = new();

        public Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody,
            string textBody,
            CancellationToken ct = default
        )
        {
            Sent.Enqueue(new CapturedEmail(toEmail, subject, htmlBody, textBody));
            return Task.CompletedTask;
        }
    }

    public sealed record CapturedEmail(string To, string Subject, string Html, string Text);

    /// <summary>In-memory <see cref="IGoogleTokenVerifier"/> fake — a test calls
    /// <see cref="Register"/> with the identity it wants to simulate and gets back an opaque
    /// "idToken" string to POST; <see cref="VerifyAsync"/> just looks it up. An unregistered
    /// string verifies as null, same as a real invalid/expired token.</summary>
    public sealed class FakeGoogleTokenVerifier : IGoogleTokenVerifier
    {
        private readonly ConcurrentDictionary<string, GoogleUserInfo> _tokens = new();

        public string Register(GoogleUserInfo info)
        {
            var token = Guid.NewGuid().ToString("N");
            _tokens[token] = info;
            return token;
        }

        public Task<GoogleUserInfo?> VerifyAsync(string idToken, CancellationToken ct = default) =>
            Task.FromResult(_tokens.GetValueOrDefault(idToken));
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
