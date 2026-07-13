namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical PushOutbox.Kind values. mention/reply/friend_request mirror the persisted
/// Notification.Type they were staged alongside; "dm" is a per-message fan-out with no
/// Notification row (offline delivery only); "call" is the offline arm of a DM/group-DM
/// ring (same participants-minus-actor fan-out as "dm").
/// </summary>
public static class PushKind
{
    public const string Mention = "mention";
    public const string Reply = "reply";
    public const string FriendRequest = "friend_request";
    public const string Dm = "dm";
    public const string Call = "call";

    // A per-message ("all" level) notification — a Notification row exists, so it's offline-gated
    // like mention/reply (not the un-suppressed "dm" fan-out).
    public const string Message = "message";

    // A friend invited the recipient to a server (invite-a-friend flow). Mirrors the guild_invite
    // Notification row.
    public const string GuildInvite = "guild_invite";
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
