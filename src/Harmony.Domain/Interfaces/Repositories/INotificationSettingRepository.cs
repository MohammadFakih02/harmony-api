using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface INotificationSettingRepository
{
    /// <summary>A single (user, scope) setting row, or null when none exists (= default level).</summary>
    Task<NotificationSetting?> GetAsync(long userId, string scopeType, long scopeId);

    /// <summary>
    /// All of one user's settings for a given scope type whose scope id is in <paramref name="scopeIds"/>.
    /// Used by the CRUD read to fetch a user's per-channel overrides for one guild in a single query.
    /// </summary>
    Task<List<NotificationSetting>> GetManyAsync(
        long userId,
        string scopeType,
        IEnumerable<long> scopeIds
    );

    /// <summary>
    /// Batch lookup for the mention fan-out: every row for the given users at EITHER the guild
    /// scope (scopeId == <paramref name="guildId"/>) or the channel scope (scopeId ==
    /// <paramref name="channelId"/>), in one query. The resolver picks channel-over-guild per user.
    /// </summary>
    Task<List<NotificationSetting>> GetForResolutionAsync(
        List<long> userIds,
        long guildId,
        long channelId
    );

    /// <summary>Upsert the level for one (user, scope). Does not call SaveChanges.</summary>
    Task UpsertAsync(long userId, string scopeType, long scopeId, string level);

    /// <summary>Remove a (user, scope) row, resetting it to the default. No-op if absent. Does not save.</summary>
    Task DeleteAsync(long userId, string scopeType, long scopeId);

    Task SaveChangesAsync();
}
