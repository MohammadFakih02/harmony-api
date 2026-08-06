using StackExchange.Redis;

namespace Harmony.Infrastructure.Redis;

/// <summary>
/// Infrastructure-scoped interface.
/// Allows us to mock the Redis connection manager in tests without
/// leaking StackExchange.Redis dependencies into the Application layer.
/// </summary>
public interface IRedisConnectionProvider
{
    IConnectionMultiplexer? Connection { get; }
    bool IsConnected { get; }
}
