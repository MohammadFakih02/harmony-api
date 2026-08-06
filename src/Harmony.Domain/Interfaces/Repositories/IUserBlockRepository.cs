using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IUserBlockRepository
{
    Task<UserBlock?> GetAsync(long blockerId, long blockedId);

    Task<List<UserBlock>> GetByBlockerAsync(long blockerId);

    /// <summary>
    /// True if either user has blocked the other (block is enforced in both directions).
    /// This is the seam Phase 4 DM/mention/presence features consume to suppress
    /// interaction between two users — no consumer exists yet.
    /// </summary>
    Task<bool> AreBlockedAsync(long userA, long userB);

    Task AddAsync(UserBlock block);

    void Remove(UserBlock block);

    Task SaveChangesAsync();
}
