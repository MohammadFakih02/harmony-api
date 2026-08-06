using Harmony.Application.Interfaces.Services;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// SemaphoreSlim-backed <see cref="IPushDispatchNudge"/>, capacity 1 so a burst of
/// producer signals coalesces into a single wake-up (the dispatcher drains ALL due rows
/// per cycle, so one wake-up covers the whole burst). Singleton.
/// </summary>
public sealed class PushDispatchNudge : IPushDispatchNudge
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled — the pending wake-up covers this producer too.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _signal.WaitAsync(timeout, ct);
}
