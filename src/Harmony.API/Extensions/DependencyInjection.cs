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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(2),
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
        services.AddSingleton<IMessagePublisher, RabbitMQPublisher>();

        // -----------------------------------------------------------------------
        // Redis — shared connection via IRedisConnectionProvider
        //
        // RedisConnectionProvider owns the single IConnectionMultiplexer for the
        // whole process. Everything that needs Redis (deduplicator, future unread
        // counts, presence) injects IRedisConnectionProvider — never the raw
        // IConnectionMultiplexer — so the null/unavailable case is handled explicitly.
        // -----------------------------------------------------------------------
        services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();

        // -----------------------------------------------------------------------
        // SignalR + Redis backplane
        // -----------------------------------------------------------------------
        var signalRBuilder = services.AddSignalR(options =>
        {
            options.EnableDetailedErrors =
                env.Equals("Development", StringComparison.OrdinalIgnoreCase) || isTest;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            options.AddFilter<HubExceptionFilter>();
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
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IReadStateRepository, ReadStateRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        // Application & infrastructure services
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMessageService, MessageService>();

        // RabbitMQ consumers and handlers
        services.AddScoped<IMessageConsumerHandler, MessageConsumerHandler>();
        services.AddScoped<SearchIndexConsumerHandler>();
        services.AddHostedService<ScyllaMessageConsumer>();
        services.AddHostedService<SearchIndexConsumer>();

        // Background workers
        if (!isTest)
        {
            services.AddHostedService<TokenPruningService>();
        }

        return services;
    }
}
