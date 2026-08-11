using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// Implementation of the D0 liveness grain — returns an identifying string so the health check's
/// payload confirms which grain key actually answered (not just that a call completed).
/// </summary>
public sealed class PingGrain : Grain, IPingGrain
{
    public Task<string> Ping() =>
        Task.FromResult($"pong from '{this.GetPrimaryKeyString()}' @ {DateTime.UtcNow:O}");
}
