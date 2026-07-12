using Harmony.Application.Interfaces.Services;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// <see cref="ILiveKitRoomService"/> over <see cref="RoomServiceClient"/> — with
/// <see cref="LiveKitTokenService"/>, the only files touching Livekit.Server.Sdk.Dotnet (SDK
/// containment, same as S3FileStorageService/WebPushSender). The configured <c>Host</c> is the
/// wss:// URL clients connect to; the REST API lives on the same host over https, so the scheme
/// is rewritten here. Singleton — the underlying client is a thin HttpClient wrapper.
///
/// Fail-open by design: every method catches, logs, and returns — a LiveKit API hiccup must never
/// break the hub's soft moderation path (the Redis flags + broadcasts have already landed).
/// </summary>
public sealed class LiveKitRoomService : ILiveKitRoomService
{
    private readonly RoomServiceClient? _client;
    private readonly ILogger<LiveKitRoomService> _logger;

    public LiveKitRoomService(IConfiguration configuration, ILogger<LiveKitRoomService> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("LiveKit");
        var apiKey = section["ApiKey"] ?? "";
        var apiSecret = section["ApiSecret"] ?? "";
        var host = section["Host"] ?? "";

        if (
            string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(apiSecret)
            || string.IsNullOrWhiteSpace(host)
        )
        {
            _logger.LogWarning(
                "LiveKit: ApiKey/ApiSecret/Host not configured — hard voice moderation disabled"
            );
            return;
        }

        _client = new RoomServiceClient(ToHttpHost(host), apiKey, apiSecret);
    }

    public async Task SetMicrophoneMutedAsync(
        long channelId,
        long userId,
        bool muted,
        CancellationToken ct = default
    )
    {
        if (_client is null)
            return;

        try
        {
            var participant = await _client.GetParticipant(
                new RoomParticipantIdentity
                {
                    Room = channelId.ToString(),
                    Identity = userId.ToString(),
                }
            );

            // Revoke/restore the microphone publish grant — muting the published track alone is
            // not enough, a modified client could simply unmute its own track again. An empty
            // CanPublishSources means "unrestricted", so it is made explicit before subtracting.
            var permission = participant.Permission?.Clone() ?? new ParticipantPermission();
            if (permission.CanPublishSources.Count == 0)
                permission.CanPublishSources.AddRange(
                    [
                        TrackSource.Microphone,
                        TrackSource.Camera,
                        TrackSource.ScreenShare,
                        TrackSource.ScreenShareAudio,
                    ]
                );
            if (muted)
                permission.CanPublishSources.Remove(TrackSource.Microphone);
            else if (!permission.CanPublishSources.Contains(TrackSource.Microphone))
                permission.CanPublishSources.Add(TrackSource.Microphone);

            await _client.UpdateParticipant(
                new UpdateParticipantRequest
                {
                    Room = channelId.ToString(),
                    Identity = userId.ToString(),
                    Permission = permission,
                }
            );

            if (muted)
            {
                var mic = participant.Tracks.FirstOrDefault(t =>
                    t.Source == TrackSource.Microphone
                );
                if (mic is not null)
                    await _client.MutePublishedTrack(
                        new MuteRoomTrackRequest
                        {
                            Room = channelId.ToString(),
                            Identity = userId.ToString(),
                            TrackSid = mic.Sid,
                            Muted = true,
                        }
                    );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LiveKit: server-mute({Muted}) failed for user {UserId} room {ChannelId} — soft enforcement only",
                muted,
                userId,
                channelId
            );
        }
    }

    public async Task SetCanSubscribeAsync(
        long channelId,
        long userId,
        bool canSubscribe,
        CancellationToken ct = default
    )
    {
        if (_client is null)
            return;

        try
        {
            // Permissions replace wholesale on update — read the current set first so the
            // publish-source grants from the token survive the deafen toggle.
            var participant = await _client.GetParticipant(
                new RoomParticipantIdentity
                {
                    Room = channelId.ToString(),
                    Identity = userId.ToString(),
                }
            );

            var permission = participant.Permission?.Clone() ?? new ParticipantPermission();
            permission.CanSubscribe = canSubscribe;

            await _client.UpdateParticipant(
                new UpdateParticipantRequest
                {
                    Room = channelId.ToString(),
                    Identity = userId.ToString(),
                    Permission = permission,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LiveKit: server-deafen(canSubscribe={CanSubscribe}) failed for user {UserId} room {ChannelId} — soft enforcement only",
                canSubscribe,
                userId,
                channelId
            );
        }
    }

    public async Task RemoveParticipantAsync(
        long channelId,
        long userId,
        CancellationToken ct = default
    )
    {
        if (_client is null)
            return;

        try
        {
            await _client.RemoveParticipant(
                new RoomParticipantIdentity
                {
                    Room = channelId.ToString(),
                    Identity = userId.ToString(),
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LiveKit: remove failed for user {UserId} room {ChannelId} — continuing",
                userId,
                channelId
            );
        }
    }

    /// <summary>wss://host → https://host (ws → http) — the REST API shares the Cloud host.</summary>
    private static string ToHttpHost(string host) =>
        host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            ? string.Concat("https://", host.AsSpan("wss://".Length))
            : host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                ? string.Concat("http://", host.AsSpan("ws://".Length))
                : host;
}
