using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// Trivial liveness grain (Track D0). Its only purpose is to prove the co-hosted silo can activate a
/// grain and round-trip a call; <see cref="OrleansSiloHealthCheck"/> pings it so <c>/health</c> reports
/// the silo's status alongside the Postgres/Scylla/RabbitMQ/Redis checks. No domain logic lives here —
/// the real grains (presence in D1, then channel/guild) arrive in later slices.
/// </summary>
public interface IPingGrain : IGrainWithStringKey
{
    Task<string> Ping();
}
