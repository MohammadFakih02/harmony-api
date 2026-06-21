using Cassandra;
using Harmony.API.Filters;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
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
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using RabbitMQ.Client.Exceptions;

namespace Harmony.Infrastructure.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var env =
            configuration["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        bool isTest =
            env.Equals("Test", StringComparison.OrdinalIgnoreCase)
            || AppDomain
                .CurrentDomain.GetAssemblies()
                .Any(a => a.FullName!.Contains("xunit", StringComparison.OrdinalIgnoreCase));

        // -----------------------------------------------------------------------
        // PostgreSQL (With Global Split Queries configured to prevent Cartesian warnings)
        // -----------------------------------------------------------------------
        services.AddDbContext<HarmonyDbContext>(options =>
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
        var signalRBuilder = services.AddSignalR(options =>
        {
            options.EnableDetailedErrors =
                env.Equals("Development", StringComparison.OrdinalIgnoreCase) || isTest;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            // Rate-limit before the exception filter so a rejected (throttled) call is
            // still surfaced to the client through the normal hub error path. Disabled
            // under Test (same posture as the HTTP rate limiter) so message-burst tests
            // aren't throttled by the real test Redis.
            if (!isTest)
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
                }
            );
        }

        // -----------------------------------------------------------------------
        // Message deduplication — shares the IRedisConnectionProvider connection
        // -----------------------------------------------------------------------
        services.AddSingleton<IMessageDeduplicator, RedisMessageDeduplicator>();

        // Repositories
        services.AddScoped<IGuildRepository, GuildRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IChannelPermissionOverrideRepository, ChannelPermissionOverrideRepository>();
        // Concrete registered for DI resolution by the decorator factory below.
        services.AddScoped<MessageRepository>();
        services.AddScoped<IReadStateRepository, ReadStateRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IUserBlockRepository, UserBlockRepository>();
        services.AddScoped<IUserMuteRepository, UserMuteRepository>();
        services.AddScoped<IFriendRepository, FriendRepository>();
        services.AddScoped<IDirectMessageRepository, DirectMessageRepository>();

        // Application & infrastructure services
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMessageService, MessageService>();
        services.AddScoped<IUnreadCountService, RedisUnreadCountService>();
        services.AddScoped<IPresenceService, RedisPresenceService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IFileService, FileService>();

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
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<NoHostAvailableException>()
                    .Handle<OperationTimedOutException>(),
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
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
                    scyllaCircuitLogger?.LogInformation("Scylla circuit CLOSED — reads resuming");
                    return default;
                },
                OnHalfOpened = args =>
                {
                    scyllaCircuitLogger?.LogInformation("Scylla circuit HALF-OPEN — probing");
                    return default;
                },
            })
            .Build();

        var rabbitPipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
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
            })
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
}

/// <summary>
/// Serializes <c>long</c> as a JSON string and reads both string and number forms.
/// Prevents JavaScript float64 precision loss for 64-bit Snowflake IDs in SignalR payloads.
/// </summary>
internal sealed class LongStringConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? long.Parse(reader.GetString()!)
            : reader.GetInt64();

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
