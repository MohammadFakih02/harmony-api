namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical PushOutbox.Kind values. mention/reply/friend_request mirror the persisted
/// Notification.Type they were staged alongside; "dm" is a per-message fan-out with no
/// Notification row (offline delivery only).
/// </summary>
public static class PushKind
{
    public const string Mention = "mention";
    public const string Reply = "reply";
    public const string FriendRequest = "friend_request";
    public const string Dm = "dm";
}

/// <summary>
/// Wakes the push dispatcher immediately after a producer commits outbox rows, so the
/// common case doesn't wait out the poll interval. Purely an in-process latency hint —
/// the dispatcher's periodic poll is the crash-recovery backstop, so a lost signal
/// (or a restart) only delays delivery, never drops it.
/// </summary>
public interface IPushDispatchNudge
{
    /// <summary>Non-blocking; safe to call from any thread.</summary>
    void Signal();

    /// <summary>Completes on a signal or when the timeout elapses, whichever is first.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}
