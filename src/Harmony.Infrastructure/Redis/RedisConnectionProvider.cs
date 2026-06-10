using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

public class RedisConnectionProvider : IRedisConnectionProvider
{
    public IConnectionMultiplexer? Connection { get; }

    public bool IsConnected => Connection is not null && Connection.IsConnected;

    public RedisConnectionProvider(
        IConfiguration configuration,
        ILogger<RedisConnectionProvider> logger
    )
    {
        Connection = RedisConnectionFactory.CreateMultiplexer(configuration, logger);
    }
}
