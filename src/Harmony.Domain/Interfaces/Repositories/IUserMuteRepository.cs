using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IUserMuteRepository
{
    Task<UserMute?> GetAsync(long userId, long targetId, string targetType);

    /// <summary>
    /// The caller's currently-active mutes (indefinite, or not yet expired at
    /// <paramref name="nowUnixMs"/>). Expired-but-not-yet-swept rows are excluded.
    /// </summary>
    Task<List<UserMute>> GetActiveMutesAsync(long userId, long nowUnixMs);

    /// <summary>
    /// True if the caller currently mutes the given target. This is the seam Phase 4
    /// notifications/typing/presence features consume to suppress output — no consumer
    /// exists yet.
    /// </summary>
    Task<bool> IsMutedAsync(long userId, long targetId, string targetType, long nowUnixMs);

    Task AddAsync(UserMute mute);

    void Remove(UserMute mute);

    /// <summary>
    /// Removes every mute whose <c>MutedUntil</c> has passed <paramref name="nowUnixMs"/>
    /// and returns the removed rows so the caller can notify each owner (MuteExpired).
    /// Indefinite mutes (null <c>MutedUntil</c>) are never swept.
    /// </summary>
    Task<List<UserMute>> DeleteExpiredAsync(long nowUnixMs);

    Task SaveChangesAsync();
}
