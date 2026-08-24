using System.Text.Json;
using System.Text.Json.Serialization;
using Cassandra;
using Harmony.API.Filters;
using Harmony.API.Grains;
using Harmony.API.SignalR;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Messaging;
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
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using RabbitMQ.Client.Exceptions;

// NOTE ON LOCATION: this file physically lives in the Harmony.API project (not Harmony.Infrastructure)
// even though it wires up Infrastructure. It has to: it references API-layer types — the SignalR hub
// filters (RateLimitHubFilter/HubExceptionFilter) and the Orleans grain dispatchers — and the
// Clean-Architecture rule forbids Harmony.Infrastructure from referencing Harmony.API (NON-NEGOTIABLE
// #3, enforced by ArchitectureTests). The composition root is the one place allowed to know every layer,
// and that place is the API edge. The namespace matches the folder (Harmony.API.Extensions), like its
// siblings (RateLimitingExtensions, OpenApiExtensions, StartupValidationExtensions).
namespace Harmony.API.Extensions;

/// <summary>
/// The infrastructure composition root. <see cref="AddInfrastructureServices"/> is a table of contents;
/// each registration lives in a focused private helper below, grouped by concern. Registration order
/// across helpers does not affect behaviour — the DI container resolves everything lazily — so the
/// grouping is purely for readability.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment
    )
    {
        // The host environment, NOT configuration["ASPNETCORE_ENVIRONMENT"]: this method runs during
        // Program's top-level statements, and under WebApplicationFactory the test factory's
        // ConfigureAppConfiguration sources are appended AFTER that point — so an eager config read here
        // saw null → "Production" and registered every !isTest-gated background service inside the test
        // host (the PushNotificationService dispatcher then drained PushOutbox rows mid-test — the §5.67
        // "unknown root cause" flake). Lazy config reads (connection strings etc.) were never affected.
        // builder.Environment is set by UseEnvironment("Test") before any user code runs, so it is
        // correct even this early.
        var env = hostEnvironment.EnvironmentName;
        bool isTest = hostEnvironment.IsEnvironment("Test");

        // Mirrors the flag Program.cs uses for the HTTP limiter — see the note there. Defaults ON: an
        // unset key must never silently disable a protection.
        bool rateLimitingEnabled = !isTest && configuration.GetValue("RateLimiting:Enabled", true);

        return services
            .AddPostgres(configuration)
            .AddScylla()
            .AddRabbitMq()
            .AddRedisServices()
            .AddRealtimeSignalR(env, isTest, rateLimitingEnabled)
            .AddRepositories()
            .AddApplicationServices(isTest)
            .AddExternalIntegrations()
            .AddBackgroundWorkers(isTest)
            .AddResilienceDecorators()
            .AddHarmonyHealthChecks();
    }

    // -----------------------------------------------------------------------
    // PostgreSQL (with global split queries configured to prevent Cartesian warnings).
    //
    // Pooled: every request resolves a scoped HarmonyDbContext, and constructing one rebuilds its
    // internal service provider, change tracker and state manager each time. AddDbContextPool keeps
    // instances alive and resets their state on return, turning that per-request construction into a
    // rent/return.
    //
    // The pattern has real preconditions and this context meets them: exactly one constructor, taking
    // only DbContextOptions<HarmonyDbContext>; no fields of its own that could leak across requests; no
    // OnConfiguring override anywhere in the solution (a pooled context is configured once, so
    // per-instance configuration would silently apply to whoever rents it next). Keep it that way —
    // adding constructor state to HarmonyDbContext breaks pooling at runtime, not at compile time.
    // -----------------------------------------------------------------------
    private static IServiceCollection AddPostgres(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
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
        return services;
    }

    // -----------------------------------------------------------------------
    // ScyllaDB — session factory, prepared statements, keyspace bootstrap.
    // -----------------------------------------------------------------------
    private static IServiceCollection AddScylla(this IServiceCollection services)
    {
        services.AddSingleton<IScyllaSessionFactory, ScyllaSessionFactory>();
        services.AddSingleton<MessageStatements>();
        services.AddSingleton<ReadStateStatements>();
        services.AddHostedService<KeyspaceInitializer>();
        return services;
    }

    // -----------------------------------------------------------------------
    // RabbitMQ — connection, publisher, and the two message consumers.
    // The concrete RabbitMQPublisher is registered for DI resolution by the resilience decorator
    // factory (see AddResilienceDecorators). The consumers are hosted services registered
    // unconditionally (unlike the polling background workers, which are gated out of Test).
    // -----------------------------------------------------------------------
    private static IServiceCollection AddRabbitMq(this IServiceCollection services)
    {
        services.AddSingleton<RabbitMQConnection>();
        services.AddSingleton<RabbitMQPublisher>();

        services.AddScoped<IMessageConsumerHandler, MessageConsumerHandler>();
        services.AddScoped<SearchIndexConsumerHandler>();
        services.AddHostedService<ScyllaMessageConsumer>();
        services.AddHostedService<SearchIndexConsumer>();
        return services;
    }

    // -----------------------------------------------------------------------
    // Redis — the shared connection plus the fail-open gates layered on it.
    //
    // RedisConnectionProvider owns the single IConnectionMultiplexer for the whole process. Everything
    // that needs Redis injects IRedisConnectionProvider — never the raw IConnectionMultiplexer — so the
    // null/unavailable case is handled explicitly.
    //
    // D2c: there is deliberately NO SignalR Redis backplane. Track D co-hosts a single Orleans silo, so
    // there is exactly one SignalR server per deployment — IHubContext fans every broadcast out
    // in-process, removing the Redis pub/sub round-trip that used to sit on every broadcast (the hot
    // path). Redis itself stays: dedup, the sender-display cache, slowmode and rate limiting all still
    // use the shared provider. Re-introducing multiple instances would need real Orleans clustering, not
    // just re-binding a backplane, so it's removed outright rather than flag-gated. The
    // StackExchangeRedis package (+ its MessagePack security pin) is deliberately kept for an easy revert.
    // -----------------------------------------------------------------------
    private static IServiceCollection AddRedisServices(this IServiceCollection services)
    {
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();

        // Message deduplication — shares the IRedisConnectionProvider connection.
        services.AddSingleton<IMessageDeduplicator, RedisMessageDeduplicator>();

        // Sender display cache — read-through cache for the username/avatar the message consumer stamps
        // on every broadcast, so the hot path skips a per-message Postgres lookup.
        services.AddSingleton<IUserDisplayCache, RedisUserDisplayCache>();

        // Slowmode cooldowns — same Redis connection, same fail-open posture.
        services.AddSingleton<ISlowmodeGate, RedisSlowmodeGate>();
        return services;
    }

    // -----------------------------------------------------------------------
    // SignalR — the real-time transport, its JSON + MessagePack protocols, and the hub rate-limit filter.
    // -----------------------------------------------------------------------
    private static IServiceCollection AddRealtimeSignalR(
        this IServiceCollection services,
        string env,
        bool isTest,
        bool rateLimitingEnabled
    )
    {
        // Hub rate-limit filter — singleton; depends only on the singleton Redis provider and logger.
        // Resolved by SignalR for the AddFilter<> registration below.
        services.AddSingleton<RateLimitHubFilter>();

        services
            .AddSignalR(options =>
            {
                options.EnableDetailedErrors =
                    env.Equals("Development", StringComparison.OrdinalIgnoreCase) || isTest;
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                // Rate-limit before the exception filter so a rejected (throttled) call is still
                // surfaced to the client through the normal hub error path. Disabled under Test (same
                // posture as the HTTP rate limiter) so message-burst tests aren't throttled by the real
                // test Redis, and by RateLimiting:Enabled=false so a load test isn't capped at
                // SendMessage's 5/s while HTTP runs unlimited.
                if (rateLimitingEnabled)
                    options.AddFilter<RateLimitHubFilter>();
                options.AddFilter<HubExceptionFilter>();
            })
            .AddJsonProtocol(options =>
            {
                // Serialize long (Snowflake IDs) as JSON strings so JavaScript clients can round-trip
                // 64-bit IDs without float64 precision loss. AllowReadingFromString lets hub method
                // params accept "123" as long.
                options.PayloadSerializerOptions.Converters.Add(new LongStringConverter());
                options.PayloadSerializerOptions.NumberHandling =
                    JsonNumberHandling.AllowReadingFromString;
            })
            // D5: also offer the binary MessagePack protocol. Both are negotiated per-connection, so a
            // JSON-only client (incl. the .NET SignalR integration-test client, which defaults to JSON)
            // is unaffected. The custom resolver keeps the snowflake-as-string wire contract (see
            // MessagePackLongAsStringResolver — the binary mirror of LongStringConverter above), and it
            // is composed ahead of ContractlessStandardResolver because the hub DTOs carry no MessagePack
            // attributes. UntrustedData enforces the depth limit that hardens the deeply-nested-array DoS
            // class (CVE-2026-45591) at the deserializer, matching the package bump.
            .AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MessagePackSerializerOptions
                    .Standard.WithResolver(
                        CompositeResolver.Create(
                            // Order matters: long/long? → string first, then Harmony DTOs as
                            // camelCase-keyed maps (matching the JSON protocol), then the standard
                            // contractless resolver for everything else (strings, collections, enums).
                            MessagePackLongAsStringResolver.Instance,
                            MessagePackCamelCaseResolver.Instance,
                            ContractlessStandardResolver.Instance
                        )
                    )
                    .WithSecurity(MessagePackSecurity.UntrustedData);
            });
        return services;
    }

    // -----------------------------------------------------------------------
    // Repositories — one scoped registration per aggregate. The concrete MessageRepository is registered
    // (not just its interface) so the resilience decorator factory can resolve the inner instance.
    // -----------------------------------------------------------------------
    private static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddScoped<IGuildRepository, GuildRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<
            IChannelPermissionOverrideRepository,
            ChannelPermissionOverrideRepository
        >();
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
        services.AddScoped<IFileAttachmentRepository, FileAttachmentRepository>();
        return services;
    }

    // -----------------------------------------------------------------------
    // Application & domain services. Three of these swap implementation by environment: outside Test
    // they are grain-backed (Track D), inside Test they keep the pre-grain implementation because no
    // Orleans silo is co-hosted in the integration host (D0 gated it out to avoid the parallel-host port
    // collision). The IPresenceService / IChannelDispatcher / IPermissionService seams and every call
    // site are identical across both — only the registration differs.
    // -----------------------------------------------------------------------
    private static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        bool isTest
    )
    {
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMessageService, MessageService>();
        services.AddScoped<IUnreadCountService, RedisUnreadCountService>();

        // Presence is grain-backed outside Test (Track D1): GrainPresenceService forwards to the per-user
        // UserGrain, whose in-memory connection set replaces the Redis presence keys + the global sweep.
        // The Test environment keeps RedisPresenceService so the ~450 hub integration tests exercise the
        // unchanged Redis path; the grain path has its own InProcessTestCluster tests.
        if (isTest)
            services.AddScoped<IPresenceService, RedisPresenceService>();
        else
            services.AddScoped<IPresenceService, GrainPresenceService>();

        // Message send fan-out is grain-backed outside Test (Track D2a): GrainChannelDispatcher routes
        // each send to the per-channel ChannelGrain, which broadcasts first and then republishes the
        // event as the durable RabbitMQ persist log. The Test environment keeps LegacyChannelDispatcher
        // (publish → consumer broadcasts). Both impls are stateless singletons.
        if (isTest)
            services.AddSingleton<IChannelDispatcher, LegacyChannelDispatcher>();
        else
            services.AddSingleton<IChannelDispatcher, GrainChannelDispatcher>();

        services.AddScoped<IVoiceStateService, RedisVoiceStateService>();

        // Permissions are grain-backed outside Test (Track D4): GrainPermissionService forwards to the
        // per-guild GuildGrain, whose in-memory snapshot (roles + membership + overrides) replaces the
        // Redis perms:{u}:{g} cache and resolves in-process — the unread fan-out's FilterByPermission
        // becomes a pure in-memory pass. The Test environment keeps the Redis-cached PermissionService.
        if (isTest)
            services.AddScoped<IPermissionService, PermissionService>();
        else
            services.AddScoped<IPermissionService, GrainPermissionService>();

        services.AddScoped<IFileService, FileService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IGuildMemberService, GuildMemberService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<ISearchService, SearchService>();
        return services;
    }

    // -----------------------------------------------------------------------
    // External integrations — each owns its third-party client behind an interface, so the SDK stays
    // confined to Infrastructure. All singletons: immutable config, thread-safe, resolved from the
    // relevant appsettings section (WebPush / Smtp / Google / LiveKit / ObjectStorage).
    // -----------------------------------------------------------------------
    private static IServiceCollection AddExternalIntegrations(this IServiceCollection services)
    {
        // Web push — the sender owns the VAPID client; the nudge is the producers' wake-up line to the
        // dispatcher.
        services.AddSingleton<IWebPushSender, WebPushSender>();
        services.AddSingleton<IPushDispatchNudge, PushDispatchNudge>();

        // Email — the sender owns the SMTP client (MailKit); the cooldown gate shares the same Redis
        // connection as every other gate.
        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        services.AddSingleton<IEmailCooldownGate, RedisEmailCooldownGate>();

        // Google sign-in — verifies ID tokens from the frontend's Google Identity Services button.
        services.AddSingleton<IGoogleTokenVerifier, GoogleTokenVerifier>();

        // Email-code 2FA challenge store — fails CLOSED (unlike every cooldown/dedup gate above), so it's
        // kept separate from the email plumbing rather than folded into it.
        services.AddSingleton<ITwoFactorChallengeStore, RedisTwoFactorChallengeStore>();

        // Voice — the token service owns the LiveKit signing keys. Hard voice moderation (server
        // mute/deafen/move) goes over the LiveKit server API — fail-open, silent no-op when unconfigured
        // (CI / fresh checkout).
        services.AddSingleton<ILiveKitTokenService, LiveKitTokenService>();
        services.AddSingleton<ILiveKitRoomService, LiveKitRoomService>();

        // File storage — S3FileStorageService builds its own IAmazonS3 from the ObjectStorage section,
        // so the AWS SDK types stay confined to Infrastructure. (The FileAttachment repository is
        // registered with the other repositories.)
        services.AddSingleton<IFileStorageService, S3FileStorageService>();
        services.AddHostedService<ObjectStorageBucketInitializer>();
        return services;
    }

    // -----------------------------------------------------------------------
    // Background workers — polling sweeps, all gated OUT of Test (the integration host would otherwise
    // run them against the shared test infrastructure and interfere with deterministic tests; e.g. the
    // PushNotificationService dispatcher draining PushOutbox rows mid-test).
    //
    // PresenceSweepService was retired under D1 — each UserGrain prunes its own stale connections on a
    // grain timer, so there is no global presence:online ZSET to sweep.
    // -----------------------------------------------------------------------
    private static IServiceCollection AddBackgroundWorkers(
        this IServiceCollection services,
        bool isTest
    )
    {
        if (isTest)
            return services;

        services.AddHostedService<TokenPruningService>();
        services.AddHostedService<MuteExpiryService>();
        services.AddHostedService<OrphanFileSweepService>();
        services.AddHostedService<StatusExpiryService>();
        services.AddHostedService<VoiceStateSweepService>();
        services.AddHostedService<InviteCleanupService>();
        services.AddHostedService<PushNotificationService>();
        services.AddHostedService<TrashPurgeService>();
        return services;
    }

    // -----------------------------------------------------------------------
    // Circuit breakers + the decorators that use them. Kept together deliberately: each decorator
    // registration captures its pipeline (and a lazily-set logger) in a closure, so the pipeline must be
    // built in the same scope. Each pipeline tracks its own failure window; Scylla and RabbitMQ failures
    // never pollute each other's counters. The pipelines are effectively singletons (captured once); the
    // decorators match their inner service's lifetime (MessageRepository scoped, RabbitMQPublisher
    // singleton).
    // -----------------------------------------------------------------------
    private static IServiceCollection AddResilienceDecorators(this IServiceCollection services)
    {
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
                    // Short break so reads resume within a few seconds of Scylla recovering — a longer
                    // window made a recovered node take "several refreshes" to serve history again while
                    // the breaker stayed open. The half-open probe still guards against re-hammering a
                    // node that hasn't actually come back.
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

    // -----------------------------------------------------------------------
    // Health checks — /health is mapped in Program.cs. Postgres/Scylla/RabbitMQ are core dependencies
    // (Unhealthy → 503 → ALB pulls the task); Redis and the DLQ-depth check report Degraded (still 200)
    // since the app is designed to keep serving through both (§18/§19).
    // -----------------------------------------------------------------------
    private static IServiceCollection AddHarmonyHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres")
            .AddCheck<RedisHealthCheck>("redis")
            .AddCheck<ScyllaHealthCheck>("scylla")
            .AddCheck<RabbitMqHealthCheck>("rabbitmq")
            .AddCheck<DeadLetterQueueHealthCheck>("dead-letter-queue");
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
