using System.Text.Json;
using System.Text.Json.Serialization;
using Cassandra;
using Harmony.API.Filters;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Harmony.Infrastructure.HealthChecks;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.Postgres.Repositories;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.RabbitMQ.Consumers;
using Harmony.Infrastructure.RabbitMQ.Producers;
using Harmony.Infrastructure.Redis;
using Harmony.Infrastructure.Scylla;
using Harmony.Infrastructure.Scylla.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using RabbitMQ.Client.Exceptions;

namespace Harmony.Infrastructure.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment
    )
    {
        // The host environment, NOT configuration["ASPNETCORE_ENVIRONMENT"]: this method runs
        // during Program's top-level statements, and under WebApplicationFactory the test
        // factory's ConfigureAppConfiguration sources are appended AFTER that point — so an
        // eager config read here saw null → "Production" and registered every !isTest-gated
        // background service inside the test host (the PushNotificationService dispatcher then
        // drained PushOutbox rows mid-test — the §5.67 "unknown root cause" flake). Lazy config
        // reads (connection strings etc.) were never affected. builder.Environment is set by
        // UseEnvironment("Test") before any user code runs, so it is correct even this early.
        var env = hostEnvironment.EnvironmentName;
        bool isTest = hostEnvironment.IsEnvironment("Test");

        // Mirrors the flag Program.cs uses for the HTTP limiter — see the note there. Defaults ON:
        // an unset key must never silently disable a protection.
        bool rateLimitingEnabled = !isTest && configuration.GetValue("RateLimiting:Enabled", true);

        // -----------------------------------------------------------------------
        // PostgreSQL (With Global Split Queries configured to prevent Cartesian warnings)
        //
        // Pooled: every request resolves a scoped HarmonyDbContext, and constructing one rebuilds
        // its internal service provider, change tracker and state manager each time.
        // AddDbContextPool keeps instances alive and resets their state on return, turning that
        // per-request construction into a rent/return.
        //
        // The pattern has real preconditions and this context meets them: exactly one constructor,
        // taking only DbContextOptions<HarmonyDbContext>; no fields of its own that could leak
        // across requests; no OnConfiguring override anywhere in the solution (a pooled context is
        // configured once, so per-instance configuration would silently apply to whoever rents it
        // next). Keep it that way — adding constructor state to HarmonyDbContext breaks pooling at
        // runtime, not at compile time.
        // -----------------------------------------------------------------------
        services.AddDbContextPool<HarmonyDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Postgres"),
                npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null
                    );
                    npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                }
            )
        );

        // -----------------------------------------------------------------------
        // ScyllaDB
        // -----------------------------------------------------------------------
        services.AddSingleton<IScyllaSessionFactory, ScyllaSessionFactory>();
        services.AddSingleton<MessageStatements>();
        services.AddSingleton<ReadStateStatements>();
        services.AddHostedService<KeyspaceInitializer>();

        // -----------------------------------------------------------------------
        // RabbitMQ
        // -----------------------------------------------------------------------
        services.AddSingleton<RabbitMQConnection>();
        // Concrete registered for DI resolution by the decorator factory below.
        services.AddSingleton<RabbitMQPublisher>();

        // -----------------------------------------------------------------------
        // Redis — shared connection via IRedisConnectionProvider
        //
        // RedisConnectionProvider owns the single IConnectionMultiplexer for the
        // whole process. Everything that needs Redis (deduplicator, future unread
        // counts, presence) injects IRedisConnectionProvider — never the raw
        // IConnectionMultiplexer — so the null/unavailable case is handled explicitly.
        // -----------------------------------------------------------------------
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();

        // Hub rate-limit filter — singleton; depends only on the singleton Redis
        // provider and logger. Resolved by SignalR for the AddFilter<> registration.
        services.AddSingleton<RateLimitHubFilter>();

        // -----------------------------------------------------------------------
        // SignalR + Redis backplane
        // -----------------------------------------------------------------------
        var signalRBuilder = services
            .AddSignalR(options =>
            {
                options.EnableDetailedErrors =
                    env.Equals("Development", StringComparison.OrdinalIgnoreCase) || isTest;
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                // Rate-limit before the exception filter so a rejected (throttled) call is
                // still surfaced to the client through the normal hub error path. Disabled
                // under Test (same posture as the HTTP rate limiter) so message-burst tests
                // aren't throttled by the real test Redis, and by RateLimiting:Enabled=false so
                // a load test isn't capped at SendMessage's 5/s while HTTP runs unlimited.
                if (rateLimitingEnabled)
                    options.AddFilter<RateLimitHubFilter>();
                options.AddFilter<HubExceptionFilter>();
            })
            .AddJsonProtocol(options =>
            {
                // Serialize long (Snowflake IDs) as JSON strings so JavaScript clients
                // can round-trip 64-bit IDs without float64 precision loss.
                // AllowReadingFromString lets hub method params accept "123" as long.
                options.PayloadSerializerOptions.Converters.Add(new LongStringConverter());
                options.PayloadSerializerOptions.NumberHandling =
                    JsonNumberHandling.AllowReadingFromString;
            });

        var redisConnectionString = configuration.GetConnectionString("Redis");

        if (!string.IsNullOrWhiteSpace(redisConnectionString) && !isTest)
        {
            signalRBuilder.AddStackExchangeRedis(
                redisConnectionString,
                options =>
                {
                    options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal(
                        "harmony"
                    );
                    // Same fast-fail posture as the shared multiplexer (RedisConnectionFactory):
                    // don't let a downed backplane block hub broadcasts on the 5s defaults.
                    options.Configuration.AbortOnConnectFail = false;
                    if (options.Configuration.ConnectTimeout >= 5000)
                        options.Configuration.ConnectTimeout = 2000;
                    if (options.Configuration.SyncTimeout >= 5000)
                        options.Configuration.SyncTimeout = 1000;
                    if (options.Configuration.ConnectRetry > 1)
                        options.Configuration.ConnectRetry = 1;
                }
            );
        }

        // -----------------------------------------------------------------------
        // Message deduplication — shares the IRedisConnectionProvider connection
        // -----------------------------------------------------------------------
        services.AddSingleton<IMessageDeduplicator, RedisMessageDeduplicator>();

        // Sender display cache — shared read-through cache for the username/avatar the message
        // consumer stamps on every broadcast, so the hot path skips a per-message Postgres lookup.
        services.AddSingleton<IUserDisplayCache, RedisUserDisplayCache>();

        // Slowmode cooldowns — same Redis connection, same fail-open posture
        services.AddSingleton<ISlowmodeGate, RedisSlowmodeGate>();

        // Repositories
        services.AddScoped<IGuildRepository, GuildRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<
            IChannelPermissionOverrideRepository,
            ChannelPermissionOverrideRepository
        >();
        // Concrete registered for DI resolution by the decorator factory below.
        services.AddScoped<MessageRepository>();
        services.AddScoped<IReadStateRepository, ReadStateRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<ITrustedDeviceRepository, TrustedDeviceRepository>();
        services.AddScoped<IUserBlockRepository, UserBlockRepository>();
        services.AddScoped<IUserMuteRepository, UserMuteRepository>();
        services.AddScoped<IFriendRepository, FriendRepository>();
        services.AddScoped<IUserNicknameRepository, UserNicknameRepository>();
        services.AddScoped<IDirectMessageRepository, DirectMessageRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationPreferenceRepository, NotificationPreferenceRepository>();
        services.AddScoped<INotificationSettingRepository, NotificationSettingRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IGuildInviteRepository, GuildInviteRepository>();
        services.AddScoped<IGuildBanRepository, GuildBanRepository>();
        services.AddScoped<IMessageSearchRepository, MessageSearchRepository>();
        services.AddScoped<IMessageReactionRepository, MessageReactionRepository>();
        services.AddScoped<IPushOutboxRepository, PushOutboxRepository>();
        services.AddScoped<IPushSubscriptionRepository, PushSubscriptionRepository>();

        // Application & infrastructure services
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMessageService, MessageService>();
        services.AddScoped<IUnreadCountService, RedisUnreadCountService>();
        services.AddScoped<IPresenceService, RedisPresenceService>();
        services.AddScoped<IVoiceStateService, RedisVoiceStateService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IFileService, FileService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IGuildMemberService, GuildMemberService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<ISearchService, SearchService>();

        // Web push — the sender owns the VAPID client (WebPush config section, SDK confined
        // to Infrastructure); the nudge is the producers' wake-up line to the dispatcher.
        services.AddSingleton<IWebPushSender, WebPushSender>();
        services.AddSingleton<IPushDispatchNudge, PushDispatchNudge>();

        // Email — the sender owns the SMTP client (Smtp config section, MailKit confined to
        // Infrastructure); the cooldown gate shares the same Redis connection as every other gate.
        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        services.AddSingleton<IEmailCooldownGate, RedisEmailCooldownGate>();

        // Google sign-in — verifies ID tokens from the frontend's Google Identity Services button
        // (Google config section, Google.Apis.Auth confined to Infrastructure).
        services.AddSingleton<IGoogleTokenVerifier, GoogleTokenVerifier>();

        // Email-code 2FA challenge store — fails CLOSED (unlike every cooldown/dedup gate above),
        // so it's kept separate from the email plumbing rather than folded into it.
        services.AddSingleton<ITwoFactorChallengeStore, RedisTwoFactorChallengeStore>();

        // Voice — the token service owns the LiveKit signing keys (LiveKit config section, SDK
        // confined to Infrastructure). Singleton: immutable config, thread-safe, mints per call.
        services.AddSingleton<ILiveKitTokenService, LiveKitTokenService>();
        // Hard voice moderation (server mute/deafen/move) over the LiveKit server API — fail-open,
        // silent no-op when unconfigured (CI / fresh checkout).
        services.AddSingleton<ILiveKitRoomService, LiveKitRoomService>();

        // File storage — S3FileStorageService builds its own IAmazonS3 from config (ObjectStorage
        // section), so the AWS SDK types stay confined to Infrastructure (not referenced here).
        services.AddScoped<IFileAttachmentRepository, FileAttachmentRepository>();
        services.AddSingleton<IFileStorageService, S3FileStorageService>();
        services.AddHostedService<ObjectStorageBucketInitializer>();

        // RabbitMQ consumers and handlers
        services.AddScoped<IMessageConsumerHandler, MessageConsumerHandler>();
        services.AddScoped<SearchIndexConsumerHandler>();
        services.AddHostedService<ScyllaMessageConsumer>();
        services.AddHostedService<SearchIndexConsumer>();

        // Background workers
        if (!isTest)
        {
            services.AddHostedService<TokenPruningService>();
            services.AddHostedService<MuteExpiryService>();
            services.AddHostedService<OrphanFileSweepService>();
            services.AddHostedService<StatusExpiryService>();
            services.AddHostedService<PresenceSweepService>();
            services.AddHostedService<VoiceStateSweepService>();
            services.AddHostedService<InviteCleanupService>();
            services.AddHostedService<PushNotificationService>();
            services.AddHostedService<TrashPurgeService>();
        }

        // -----------------------------------------------------------------------
        // Circuit breakers — built once (singleton lifetime via closure capture).
        // Each pipeline tracks its own failure window; Scylla and RabbitMQ
        // failures never pollute each other's counters.
        // -----------------------------------------------------------------------

        // Nullable loggers captured by the pipeline callbacks; set on first service resolution.
        ILogger<ResilientMessageRepository>? scyllaCircuitLogger = null;
        ILogger<ResilientMessagePublisher>? rabbitCircuitLogger = null;

        var scyllaPipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(
                new CircuitBreakerStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder()
                        .Handle<NoHostAvailableException>()
                        .Handle<OperationTimedOutException>(),
                    FailureRatio = 0.5,
                    MinimumThroughput = 5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    // Short break so reads resume within a few seconds of Scylla recovering —
                    // a longer window made a recovered node take "several refreshes" to serve
                    // history again while the breaker stayed open. The half-open probe still
                    // guards against re-hammering a node that hasn't actually come back.
                    BreakDuration = TimeSpan.FromSeconds(5),
                    OnOpened = args =>
                    {
                        scyllaCircuitLogger?.LogError(
                            "Scylla circuit OPENED — fast-failing reads for {BreakDuration}",
                            args.BreakDuration
                        );
                        return default;
                    },
                    OnClosed = args =>
                    {
                        scyllaCircuitLogger?.LogInformation(
                            "Scylla circuit CLOSED — reads resuming"
                        );
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        scyllaCircuitLogger?.LogInformation("Scylla circuit HALF-OPEN — probing");
                        return default;
                    },
                }
            )
            .Build();

        var rabbitPipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(
                new CircuitBreakerStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder()
                        .Handle<BrokerUnreachableException>()
                        .Handle<AlreadyClosedException>()
                        .Handle<OperationInterruptedException>(),
                    FailureRatio = 0.5,
                    MinimumThroughput = 5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(30),
                    OnOpened = args =>
                    {
                        rabbitCircuitLogger?.LogError(
                            "RabbitMQ publish circuit OPENED — fast-failing publishes for {BreakDuration}",
                            args.BreakDuration
                        );
                        return default;
                    },
                    OnClosed = args =>
                    {
                        rabbitCircuitLogger?.LogInformation(
                            "RabbitMQ publish circuit CLOSED — publishes resuming"
                        );
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        rabbitCircuitLogger?.LogInformation(
                            "RabbitMQ publish circuit HALF-OPEN — probing"
                        );
                        return default;
                    },
                }
            )
            .Build();

        // -----------------------------------------------------------------------
        // Health checks — /health is mapped in Program.cs. Postgres/Scylla/RabbitMQ are core
        // dependencies (Unhealthy → 503 → ALB pulls the task); Redis and the DLQ-depth check report
        // Degraded (still 200) since the app is designed to keep serving through both (§18/§19).
        // -----------------------------------------------------------------------
        services
            .AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres")
            .AddCheck<RedisHealthCheck>("redis")
            .AddCheck<ScyllaHealthCheck>("scylla")
            .AddCheck<RabbitMqHealthCheck>("rabbitmq")
            .AddCheck<DeadLetterQueueHealthCheck>("dead-letter-queue");

        // Scoped decorator — inner MessageRepository is scoped; pipeline is singleton.
        services.AddScoped<IMessageRepository>(sp =>
        {
            scyllaCircuitLogger ??= sp.GetRequiredService<ILogger<ResilientMessageRepository>>();
            return new ResilientMessageRepository(
                sp.GetRequiredService<MessageRepository>(),
                scyllaPipeline
            );
        });

        // Singleton decorator — inner RabbitMQPublisher is singleton; pipeline is singleton.
        services.AddSingleton<IMessagePublisher>(sp =>
        {
            rabbitCircuitLogger ??= sp.GetRequiredService<ILogger<ResilientMessagePublisher>>();
            return new ResilientMessagePublisher(
                sp.GetRequiredService<RabbitMQPublisher>(),
                rabbitPipeline
            );
        });

        return services;
    }
}

/// <summary>
/// Serializes <c>long</c> as a JSON string and reads both string and number forms.
/// Prevents JavaScript float64 precision loss for 64-bit Snowflake IDs in SignalR payloads.
/// </summary>
internal sealed class LongStringConverter : JsonConverter<long>
{
    public override long Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        reader.TokenType == JsonTokenType.String
            ? long.Parse(reader.GetString()!)
            : reader.GetInt64();

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
