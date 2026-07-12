namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Server-side LiveKit room control — the HARD enforcement layer behind voice moderation. The
/// Redis voice state + broadcasts are the soft layer every honest client obeys; these calls make
/// a modified client irrelevant (LiveKit Cloud itself mutes the track / blocks subscribing /
/// disconnects the participant). Everything is fail-open and never throws into the hub: if the
/// LiveKit API is unreachable the soft enforcement still applied, and honest clients comply.
///
/// Room name is always the channelId, participant identity always the userId (§5.57), so the
/// whole surface speaks ids. Config comes from the same <c>LiveKit</c> section as
/// <see cref="ILiveKitTokenService"/>; when unconfigured every call is a silent no-op.
/// </summary>
public interface ILiveKitRoomService
{
    /// <summary>
    /// Server-mutes/unmutes the participant's published microphone track. A no-op when they have
    /// no mic track published (nothing to enforce — the soft flag still rides the broadcasts).
    /// </summary>
    Task SetMicrophoneMutedAsync(
        long channelId,
        long userId,
        bool muted,
        CancellationToken ct = default
    );

    /// <summary>
    /// Enables/disables the participant's ability to subscribe to any track — the hard arm of a
    /// server deafen (they stop receiving audio/video at the SFU, not just in the UI). Publish
    /// grants are preserved as they are.
    /// </summary>
    Task SetCanSubscribeAsync(
        long channelId,
        long userId,
        bool canSubscribe,
        CancellationToken ct = default
    );

    /// <summary>
    /// Disconnects the participant from the room. Used after a moderator move: the honest client
    /// has already reconnected to the destination; a client that ignored VoiceForceMoved is cut
    /// from the old room's media here. Rejoining later is possible (permissions willing).
    /// </summary>
    Task RemoveParticipantAsync(long channelId, long userId, CancellationToken ct = default);
}
