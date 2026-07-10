using Harmony.Application.Hubs;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Ephemeral voice-room membership + per-participant state, modeled on <see cref="IPresenceService"/>
/// (Redis, fail-open, broadcasts through <c>IHubBroadcaster</c>). A user is in at most one room at a
/// time — joining a new one evicts the old (Discord behavior). The Postgres <c>VoiceStates</c> table
/// stays unused in v1; Redis is the runtime source of truth. Authorization is the caller's job (the
/// hub / REST endpoint gate ConnectVoice or DM-participation before invoking these).
/// </summary>
public interface IVoiceStateService
{
    /// <summary>
    /// Adds the user to the room named after <paramref name="channelId"/>, evicting any room they
    /// were already in, and broadcasts VoiceParticipantJoined. <paramref name="guildId"/> is null for
    /// a DM/group-DM call — it rides the broadcast so guild-group observers get updated too.
    /// </summary>
    Task JoinAsync(long channelId, long? guildId, long userId, CancellationToken ct = default);

    /// <summary>
    /// Removes the user from whatever room they're in (no-op if none) and broadcasts
    /// VoiceParticipantLeft. Called on manual leave, on last-connection disconnect, and by the sweep.
    /// </summary>
    Task LeaveAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Updates the caller's voice flags in their current room and broadcasts VoiceStateUpdated. No-op
    /// if the user isn't in a room. Mute/deafen/video/screenshare are all self-reported in v1
    /// (server-side moderation of others is a deferred follow-up).
    /// </summary>
    Task UpdateStateAsync(
        long userId,
        bool isMuted,
        bool isDeafened,
        bool isVideoOn,
        bool isStreaming,
        CancellationToken ct = default
    );

    /// <summary>Current participants of a room, for the initial roster load (a joiner / a sidebar view).</summary>
    Task<IReadOnlyList<VoiceParticipantPayload>> GetChannelParticipantsAsync(
        long channelId,
        CancellationToken ct = default
    );

    /// <summary>
    /// The room the user is currently in (null guildId = a DM/group-DM call), or null when not in
    /// a room — or when Redis is unavailable (fail-open, like everything here). Lets the hub
    /// resolve permissions for a state update without trusting a client-supplied channelId.
    /// </summary>
    Task<VoiceRoomRef?> GetCurrentRoomAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Reaps ghost voice states — users still listed in a room whose presence has gone offline (a
    /// crash/server-restart where the hub's OnDisconnected never ran). Returns the number reaped.
    /// Invoked by <c>VoiceStateSweepService</c>.
    /// </summary>
    Task<int> SweepGhostsAsync(CancellationToken ct = default);
}

/// <summary>A user's current voice room: the channel and, for guild channels, its guild.</summary>
public readonly record struct VoiceRoomRef(long ChannelId, long? GuildId);
