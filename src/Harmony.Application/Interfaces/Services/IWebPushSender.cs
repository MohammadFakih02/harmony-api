namespace Harmony.Application.Interfaces.Services;

/// <summary>Outcome of a single web-push delivery attempt.</summary>
public enum PushSendResult
{
    Sent,

    /// <summary>The push service says the subscription no longer exists (404/410) — prune it.</summary>
    Gone,

    /// <summary>Transient failure (network, 5xx) — the outbox row retries with backoff.</summary>
    Failed,
}

/// <summary>
/// SDK-free seam over the Web Push protocol (VAPID). The Infrastructure implementation is
/// the only code touching the push client library — same containment pattern as
/// IFileStorageService over the S3 SDK. Never throws; failures map to the result enum.
/// </summary>
public interface IWebPushSender
{
    Task<PushSendResult> SendAsync(
        string endpoint,
        string p256dh,
        string authKey,
        string payloadJson,
        CancellationToken ct = default
    );

    /// <summary>The VAPID public key the client subscribes with (empty when unconfigured).</summary>
    string PublicKey { get; }
}
