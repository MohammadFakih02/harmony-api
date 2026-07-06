using System.Net;
using Harmony.Application.Interfaces.Services;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// VAPID web-push implementation of <see cref="IWebPushSender"/> — the ONLY file touching
/// Lib.Net.Http.WebPush (same SDK-containment pattern as S3FileStorageService over the S3
/// SDK). Built from the <c>WebPush</c> config section; when the keys are unconfigured
/// (fresh checkout, CI) every send reports Failed without attempting delivery. Never
/// throws: 404/410 from the push service map to Gone (prune the subscription), everything
/// else to Failed (the outbox row retries with backoff). Singleton — the client is
/// thread-safe and holds the connection.
/// </summary>
public sealed class WebPushSender : IWebPushSender
{
    private readonly PushServiceClient? _client;
    private readonly ILogger<WebPushSender> _logger;

    public string PublicKey { get; }

    public WebPushSender(IConfiguration configuration, ILogger<WebPushSender> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("WebPush");
        PublicKey = section["PublicKey"] ?? "";
        var privateKey = section["PrivateKey"] ?? "";
        var subject = section["Subject"] ?? "mailto:admin@harmony.local";

        if (string.IsNullOrWhiteSpace(PublicKey) || string.IsNullOrWhiteSpace(privateKey))
        {
            _logger.LogWarning("WebPush: VAPID keys not configured — push delivery disabled");
            return;
        }

        _client = new PushServiceClient
        {
            DefaultAuthentication = new VapidAuthentication(PublicKey, privateKey)
            {
                Subject = subject,
            },
        };
    }

    public async Task<PushSendResult> SendAsync(
        string endpoint,
        string p256dh,
        string authKey,
        string payloadJson,
        CancellationToken ct = default
    )
    {
        if (_client is null)
            return PushSendResult.Failed;

        var subscription = new PushSubscription { Endpoint = endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, p256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, authKey);

        try
        {
            await _client.RequestPushMessageDeliveryAsync(
                subscription,
                new PushMessage(payloadJson),
                ct
            );
            return PushSendResult.Sent;
        }
        catch (PushServiceClientException ex)
            when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // The push service says this subscription no longer exists — expected churn
            // (browser reset, permission revoked), not an error. The caller prunes the row.
            return PushSendResult.Gone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebPush: delivery failed for endpoint {Endpoint}", endpoint);
            return PushSendResult.Failed;
        }
    }
}
