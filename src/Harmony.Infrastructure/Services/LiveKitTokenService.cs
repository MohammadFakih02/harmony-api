using Harmony.Application.Interfaces.Services;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// LiveKit join-token minting — the ONLY file touching Livekit.Server.Sdk.Dotnet (same
/// SDK-containment pattern as S3FileStorageService/WebPushSender). Built from the <c>LiveKit</c>
/// config section; when the keys are unconfigured (fresh checkout, CI) <see cref="IsConfigured"/>
/// is false and <see cref="CreateToken"/> returns null without attempting to sign. Singleton —
/// holds only immutable config and mints a fresh <see cref="AccessToken"/> per call.
/// </summary>
public sealed class LiveKitTokenService : ILiveKitTokenService
{
    // A voice session that outlives 6h is exceptional; the client re-fetches on (re)join, so a
    // bounded TTL limits the blast radius of a leaked token without disrupting normal calls.
    private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(6);

    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly ILogger<LiveKitTokenService> _logger;

    public bool IsConfigured { get; }
    public string Url { get; }

    public LiveKitTokenService(IConfiguration configuration, ILogger<LiveKitTokenService> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("LiveKit");
        _apiKey = section["ApiKey"] ?? "";
        _apiSecret = section["ApiSecret"] ?? "";
        Url = section["Host"] ?? "";

        IsConfigured =
            !string.IsNullOrWhiteSpace(_apiKey)
            && !string.IsNullOrWhiteSpace(_apiSecret)
            && !string.IsNullOrWhiteSpace(Url);

        if (!IsConfigured)
            _logger.LogWarning(
                "LiveKit: ApiKey/ApiSecret/Host not configured — voice token minting disabled"
            );
    }

    public string? CreateToken(long channelId, long userId, string displayName)
    {
        if (!IsConfigured)
            return null;

        try
        {
            var grants = new VideoGrants
            {
                Room = channelId.ToString(),
                RoomJoin = true,
                CanPublish = true,
                CanSubscribe = true,
                CanPublishData = true,
            };

            return new AccessToken(_apiKey, _apiSecret)
                .WithIdentity(userId.ToString())
                .WithName(displayName)
                .WithGrants(grants)
                .WithTtl(TokenTtl)
                .ToJwt();
        }
        catch (Exception ex)
        {
            // Never throw into the request pipeline — the endpoint maps a null token to a clean
            // 503 "voice unavailable" rather than a 500.
            _logger.LogError(
                ex,
                "LiveKit: failed minting token for user {UserId} in channel {ChannelId}",
                userId,
                channelId
            );
            return null;
        }
    }
}
