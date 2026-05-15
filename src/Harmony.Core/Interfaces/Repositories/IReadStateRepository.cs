namespace Harmony.Core.Interfaces.Repositories;

public interface IReadStateRepository
{
    // Gets the last read message ID for a user in a channel
    // Returns null if the user has never read the channel
    Task<long?> GetLastReadMessageIdAsync(
        long userId,
        long channelId,
        CancellationToken ct = default
    );

    // Upserts — ScyllaDB INSERT with IF NOT EXISTS is not used here
    // because we always want to overwrite with the latest read position
    Task MarkAsReadAsync(
        long userId,
        long channelId,
        long lastReadMessageId,
        CancellationToken ct = default
    );

    // Bulk fetch — used on shell load to get read states for all
    // channels the user is a member of in one query
    Task<IReadOnlyDictionary<long, long>> GetAllForUserAsync(
        long userId,
        IEnumerable<long> channelIds,
        CancellationToken ct = default
    );
}
