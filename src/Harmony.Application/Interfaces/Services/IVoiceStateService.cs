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
    /// Updates the caller's self-reported voice flags in their current room and broadcasts
    /// VoiceStateUpdated. No-op if the user isn't in a room. Server flags are untouched — those
    /// belong to <see cref="ModerateAsync"/>.
    /// </summary>
    Task UpdateStateAsync(
        long userId,
        bool isMuted,
        bool isDeafened,
        bool isVideoOn,
        bool isStreaming,
        CancellationToken ct = default
    );

    /// <summary>
    /// Sets/clears a participant's moderator-imposed server flags (null = leave that flag as is)
    /// and broadcasts VoiceStateUpdated. Applies only while the target is in
    /// <paramref name="channelId"/> (guards the moderate-vs-leave race); returns false when they
    /// aren't — or when Redis is unavailable. Authorization (MuteMembers/DeafenMembers) is the
    /// caller's job, like everything here.
    /// </summary>
    Task<bool> ModerateAsync(
        long channelId,
        long targetUserId,
        bool? serverMute,
        bool? serverDeafen,
        CancellationToken ct = default
    );

    /// <summary>
    /// Moves a participant from <paramref name="fromChannelId"/> to <paramref name="toChannelId"/>
    /// preserving ALL flags (unlike a fresh join, a moderator move carries mute/deafen/server flags
    /// across — Discord behavior), broadcasting Left(from) + Joined(to). Returns false when the
    /// target isn't currently in the source room. <paramref name="guildId"/> is the destination's.
    /// </summary>
    Task<bool> MoveAsync(
        long targetUserId,
        long fromChannelId,
        long toChannelId,
        long? guildId,
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

    /// <summary>
    /// Marks a DM/group-DM call as ringing (SET NX with a 75s TTL backstop — clients drive the real
    /// 60s timeout). False when a ring is already live for the channel; true on success — and on
    /// Redis unavailability (fail-open: the call still proceeds, just without ring bookkeeping).
    /// </summary>
    Task<bool> TryBeginRingAsync(long channelId, long callerId, CancellationToken ct = default);

    /// <summary>
    /// The userId that started the live ring on this channel, or null when no ring is live (answered,
    /// ended, expired) — or when Redis is unavailable.
    /// </summary>
    Task<long?> GetRingCallerAsync(long channelId, CancellationToken ct = default);

    /// <summary>
    /// Ends the channel's ring. True iff a live ring existed (the delete removed a key) — the
    /// "nobody ever answered" signal the missed-call message hangs on. False when there was no ring
    /// or Redis is unavailable.
    /// </summary>
    Task<bool> TryEndRingAsync(long channelId, CancellationToken ct = default);
}

/// <summary>A user's current voice room: the channel and, for guild channels, its guild.</summary>
public readonly record struct VoiceRoomRef(long ChannelId, long? GuildId);
