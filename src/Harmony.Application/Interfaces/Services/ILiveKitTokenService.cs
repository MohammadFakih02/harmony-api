namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Mints signed LiveKit join tokens for a channel-scoped room. The room name is always the
/// channelId (uniform across guild voice channels / DMs / group DMs — §5.57), so authorization
/// is the caller's job (the REST endpoint gates ConnectVoice / DM-participant before minting).
///
/// The backend only issues + signs tokens; media flows client ↔ LiveKit Cloud directly, never
/// through the API. Built from the <c>LiveKit</c> config section (Cloud keys via user-secrets in
/// dev, env vars in prod) — same containment pattern as WebPushSender over its VAPID SDK.
/// </summary>
public interface ILiveKitTokenService
{
    /// <summary>
    /// True when the LiveKit ApiKey/ApiSecret/Host are all configured. False on a fresh checkout
    /// or in CI (no Cloud keys) — callers should surface a "voice unavailable" error rather than
    /// hand out an unusable token.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The LiveKit Cloud websocket URL clients connect to (the configured <c>Host</c>). Returned
    /// alongside the token so the client needs no separate config round-trip.
    /// </summary>
    string Url { get; }

    /// <summary>
    /// Mints a join token for <paramref name="userId"/> into the room named after
    /// <paramref name="channelId"/>. Grants roomJoin + publish/subscribe/publishData, with
    /// publishing restricted to <paramref name="canPublishSources"/> (LiveKit enforces the
    /// restriction Cloud-side — a camera/screen publish outside the list is rejected). The caller
    /// resolves permissions into sources (UseVideo → camera, Stream → screen; DMs get all). The
    /// participant identity is the userId (so a second device with the same identity replaces the
    /// first — Discord's single-session-per-user voice behavior). Returns null when unconfigured.
    /// </summary>
    string? CreateToken(
        long channelId,
        long userId,
        string displayName,
        IReadOnlyList<string> canPublishSources
    );
}

/// <summary>
/// The LiveKit <c>TrackSource</c> wire names accepted in a token's <c>canPublishSources</c> grant.
/// Kept here (not Infrastructure) so the endpoint can resolve permissions → sources without
/// touching the SDK.
/// </summary>
public static class LiveKitTrackSources
{
    public const string Microphone = "microphone";
    public const string Camera = "camera";
    public const string ScreenShare = "screen_share";
    public const string ScreenShareAudio = "screen_share_audio";

    /// <summary>Every publishable source — the grant for DM/group-DM calls.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Microphone,
        Camera,
        ScreenShare,
        ScreenShareAudio,
    ];
}
