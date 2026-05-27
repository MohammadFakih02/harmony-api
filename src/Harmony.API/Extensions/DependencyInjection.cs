using System.Text;
using Harmony.Core.Interfaces;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Core.Interfaces.Services;
using Harmony.Core.Services;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.Postgres.Repositories;
using Harmony.Infrastructure.RabbitMQ;
using Harmony.Infrastructure.RabbitMQ.Consumers;
using Harmony.Infrastructure.RabbitMQ.Producers;
using Harmony.Infrastructure.Resilience;
using Harmony.Infrastructure.Scylla;
using Harmony.Infrastructure.Scylla.Repositories;
using Harmony.Infrastructure.Services;
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
        // Databases
        services.AddDbContext<HarmonyDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres"))
        );

        // ScyllaDB setup
        services.AddSingleton<IScyllaSessionFactory, ScyllaSessionFactory>();

        services.AddHostedService<KeyspaceInitializer>();

        // Singleton Resilience Policy Providers (Holds the shared Circuit Breaker states!)
        services.AddSingleton<ScyllaPolicyProvider>();
        services.AddSingleton<RabbitMQPolicyProvider>();

        // RabbitMQ setup
        services.AddSingleton<RabbitMQConnection>();
        services.AddScoped<RabbitMQPublisher>();
        services.AddScoped<IMessagePublisher>(sp => new ResilientMessagePublisher(
            sp.GetRequiredService<RabbitMQPublisher>(),
            sp.GetRequiredService<RabbitMQPolicyProvider>(), // Pass the Singleton Provider
            sp.GetRequiredService<ILogger<ResilientMessagePublisher>>()
        ));

        // Repositories
        services.AddScoped<IGuildRepository, GuildRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<MessageRepository>();
        services.AddScoped<IMessageRepository>(sp => new ResilientMessageRepository(
            sp.GetRequiredService<MessageRepository>(),
            sp.GetRequiredService<ScyllaPolicyProvider>(), // Pass the Singleton Provider
            sp.GetRequiredService<ILogger<ResilientMessageRepository>>()
        ));
        services.AddScoped<IReadStateRepository, ReadStateRepository>();

        // Core / Domain Services
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMessageService, MessageService>();

        // RabbitMQ Consumers / Handlers
        services.AddScoped<IMessageConsumerHandler, MessageConsumerHandler>();
        services.AddScoped<SearchIndexConsumerHandler>();
        services.AddHostedService<ScyllaMessageConsumer>();
        services.AddHostedService<SearchIndexConsumer>();

        return services;
    }
}
