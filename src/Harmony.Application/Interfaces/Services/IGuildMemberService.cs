using Harmony.Application.DTOs.Responses;

namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Moderation actions against guild members: kick, ban/unban, and timeout. The REST endpoints
/// gate the required permission bit declaratively ([RequirePermission]); this service owns the
/// <b>hierarchy</b> rules (can't act on yourself or the owner — role-rank comparison is deferred
/// to role-management-ui), the state mutation, the audit-log write, and the SignalR broadcasts.
/// Outcomes are signalled with the same exceptions the rest of the app uses
/// (<see cref="KeyNotFoundException"/> → 404, <see cref="UnauthorizedAccessException"/> → 403,
/// <see cref="ArgumentException"/> → 400), mapped centrally by GlobalExceptionHandler.
/// </summary>
public interface IGuildMemberService
{
    /// <summary>Removes a member from the guild (they can rejoin via a fresh invite).</summary>
    Task KickAsync(long guildId, long actorId, long targetId, CancellationToken ct = default);

    /// <summary>Removes a member and records a ban so they cannot rejoin until unbanned.</summary>
    Task BanAsync(
        long guildId,
        long actorId,
        long targetId,
        string? reason,
        CancellationToken ct = default
    );

    /// <summary>Lifts a ban, allowing the user to rejoin via an invite.</summary>
    Task UnbanAsync(long guildId, long actorId, long targetId, CancellationToken ct = default);

    /// <summary>Times a member out for <paramref name="durationSeconds"/> (max 28 days). The send
    /// gate already enforces an active timeout.</summary>
    Task TimeoutAsync(
        long guildId,
        long actorId,
        long targetId,
        long durationSeconds,
        CancellationToken ct = default
    );

    /// <summary>Clears an active timeout immediately.</summary>
    Task ClearTimeoutAsync(
        long guildId,
        long actorId,
        long targetId,
        CancellationToken ct = default
    );

    /// <summary>Lists the guild's bans (newest first), enriched with banned-user/banner identity.</summary>
    Task<IReadOnlyList<GuildBanResponse>> GetBansAsync(long guildId, CancellationToken ct = default);

    /// <summary>Sets the caller's own server nickname (always permitted for any member). Blank
    /// clears it back to the username. Broadcasts the change to the guild; no audit entry.</summary>
    Task SetOwnNicknameAsync(
        long guildId,
        long userId,
        string? nickname,
        CancellationToken ct = default
    );

    /// <summary>Sets another member's server nickname (the endpoint gates ManageNicknames). Applies
    /// the hierarchy guard (not the owner), audits a <c>member_nickname_update</c>, and broadcasts.</summary>
    Task SetNicknameAsync(
        long guildId,
        long actorId,
        long targetId,
        string? nickname,
        CancellationToken ct = default
    );
}
