namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Per-user, per-channel slowmode cooldown (flow #9 — <c>Channels.slowmode_seconds</c>).
/// Backed by Redis (<c>slowmode:{channelId}:{userId}</c>, TTL = the channel's slowmode);
/// fails open when Redis is unavailable, like every other Redis gate in the codebase.
/// </summary>
public interface ISlowmodeGate
{
    /// <summary>
    /// Attempts to consume the sender's slowmode slot. Returns <c>true</c> when the send is
    /// allowed (slot consumed / no cooldown active), <c>false</c> while the cooldown is running.
    /// </summary>
    Task<bool> TryConsumeAsync(long channelId, long userId, int slowmodeSeconds, CancellationToken ct = default);

    /// <summary>
    /// Whole seconds left on the sender's active cooldown in this channel (the key's remaining TTL),
    /// or <c>0</c> when none is running (or Redis is unavailable). Lets the client restore the
    /// countdown on channel open so leaving and rejoining no longer loses the timer.
    /// </summary>
    Task<int> GetRemainingSecondsAsync(long channelId, long userId, CancellationToken ct = default);
}
