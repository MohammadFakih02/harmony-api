using Harmony.Application.Services;
using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Domain.Interfaces.Services;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.Postgres.Repositories;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.RabbitMQ.Consumers;
using Harmony.Infrastructure.RabbitMQ.Producers;
using Harmony.Infrastructure.Scylla;
using Harmony.Infrastructure.Scylla.Repositories;
using Harmony.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harmony.Infrastructure.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // -----------------------------------------------------------------------
        // PostgreSQL
        // -----------------------------------------------------------------------
        services.AddDbContext<HarmonyDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Postgres"),
                npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(2),
                        errorCodesToAdd: null
                    )
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
        // SignalR + Redis backplane
        //
        // The Redis backplane distributes hub group membership and broadcasts
        // across all API instances. Every instance publishes to Redis; every
        // instance receives from Redis and forwards to its local WebSocket clients.
        //
        // Without this, JoinChannel on instance A and a broadcast from instance B
        // would never reach the client — a hard failure in any multi-pod deploy.
        //
        // Connection string key: ConnectionStrings:Redis
        // Expected format: "localhost:6379,password=secret,ssl=false,abortConnect=false"
        //
        // The "abortConnect=false" flag is critical — it prevents StackExchange.Redis
        // from throwing on startup if Redis is momentarily unavailable, instead
        // retrying in the background. Without it a Redis blip kills the entire pod.
        // -----------------------------------------------------------------------
        var redisConnectionString = configuration.GetConnectionString("Redis");

        var signalRBuilder = services.AddSignalR(options =>
        {
            options.EnableDetailedErrors =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development"
                || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test";

            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
        });

        // Only wire the backplane when a Redis connection string is provided.
        // In tests, HarmonyWebApplicationFactory sets this to null/empty so
        // the in-process backplane is used — no Redis instance required.
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            signalRBuilder.AddStackExchangeRedis(redisConnectionString, options =>
            {
                // Channel prefix isolates this app's messages from anything else
                // sharing the same Redis instance (staging, other services, etc.)
                options.Configuration.ChannelPrefix =
                    StackExchange.Redis.RedisChannel.Literal("harmony");
            });
        }

        // -----------------------------------------------------------------------
        // Repositories
        // -----------------------------------------------------------------------
        services.AddScoped<IGuildRepository, GuildRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IReadStateRepository, ReadStateRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        // -----------------------------------------------------------------------
        // Application & infrastructure services
        // -----------------------------------------------------------------------
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMessageService, MessageService>();

        // -----------------------------------------------------------------------
        // RabbitMQ consumers and handlers
        // -----------------------------------------------------------------------
        services.AddScoped<IMessageConsumerHandler, MessageConsumerHandler>();
        services.AddScoped<SearchIndexConsumerHandler>();
        services.AddHostedService<ScyllaMessageConsumer>();
        services.AddHostedService<SearchIndexConsumer>();

        // -----------------------------------------------------------------------
        // Background workers
        // -----------------------------------------------------------------------
        services.AddHostedService<TokenPruningService>();

        return services;
    }
}
