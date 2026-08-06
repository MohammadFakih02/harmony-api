using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface INotificationRepository
{
    /// <summary>Most recent notifications for the user, newest first.</summary>
    Task<List<Notification>> GetForUserAsync(long userId, int limit);

    /// <summary>Count only — does not materialize the rows.</summary>
    Task<int> GetUnreadCountAsync(long userId);

    /// <summary>
    /// The single notification scoped to its owner. Returns null both when the id
    /// doesn't exist and when it exists but belongs to someone else — the caller
    /// can't tell the two apart, which is the point: it bakes the ownership check
    /// into the query instead of relying on a separate check after the fetch.
    /// </summary>
    Task<Notification?> GetByIdForUserAsync(long notificationId, long userId);

    /// <summary>
    /// Bulk-marks every unread notification for the user as read in one operation —
    /// unlike single-row mark-read there's no per-row ownership ambiguity to resolve,
    /// so this doesn't need the fetch-then-mutate split GetByIdForUserAsync exists for.
    /// </summary>
    Task MarkAllReadAsync(long userId);

    /// <summary>
    /// Deletes the notification scoped to its owner. Returns true if a row was removed,
    /// false when the id doesn't exist or belongs to someone else — same owner-baked-into-
    /// the-query approach as GetByIdForUserAsync, so a caller can 404 without leaking
    /// whether another user's id is valid.
    /// </summary>
    Task<bool> DeleteForUserAsync(long notificationId, long userId);

    /// <summary>Deletes every notification belonging to the user ("clear all").</summary>
    Task DeleteAllForUserAsync(long userId);

    /// <summary>
    /// Marks the user's channel-scoped notifications (mention / reply / message — anything carrying a
    /// message id in this channel) read up to and including <paramref name="uptoMessageId"/>. Called
    /// when the user reads the channel, so opening a channel clears its bell entries the same way it
    /// clears the unread badge. Returns the number of rows actually flipped (0 = nothing to broadcast).
    /// </summary>
    Task<int> MarkChannelReadAsync(long userId, long channelId, long uptoMessageId);

    /// <summary>
    /// Marks the user's unread <c>guild_invite</c> notifications for a specific guild read — called
    /// when they join that guild, so an accepted invite stops showing in the bell. Returns the number
    /// of rows flipped.
    /// </summary>
    Task<int> MarkGuildInviteReadAsync(long userId, long guildId);

    Task AddAsync(Notification notification);

    Task SaveChangesAsync();
}
