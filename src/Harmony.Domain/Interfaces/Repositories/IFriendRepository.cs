using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IFriendRepository
{
    /// <summary>
    /// The single friendship row between two users, in whichever direction it was
    /// created (PK is (RequesterId, AddresseeId)). Returns null if no row exists.
    /// </summary>
    Task<Friend?> GetBetweenAsync(long userA, long userB);

    /// <summary>
    /// Ids of every user the given user is <c>accepted</c> friends with (either
    /// direction). This is the lookup that fills RedisPresenceService's friend-
    /// recipient seam so presence/status broadcasts actually reach friends.
    /// </summary>
    Task<List<long>> GetFriendIdsAsync(long userId);

    /// <summary>Accepted friendship rows involving the user (either direction).</summary>
    Task<List<Friend>> GetAcceptedAsync(long userId);

    /// <summary>Pending rows involving the user — both incoming and outgoing.</summary>
    Task<List<Friend>> GetPendingForAsync(long userId);

    Task AddAsync(Friend friend);

    void Remove(Friend friend);

    Task SaveChangesAsync();
}
