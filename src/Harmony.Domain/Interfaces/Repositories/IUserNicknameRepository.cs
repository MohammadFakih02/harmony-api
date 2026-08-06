using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

/// <summary>
/// Private, one-directional aliases a user has set for other users (the friend/DM display name).
/// Every read is owner-scoped — a nickname is only ever visible to the owner who set it.
/// </summary>
public interface IUserNicknameRepository
{
    /// <summary>The owner's alias for one target, or null if none is set.</summary>
    Task<UserNickname?> GetAsync(long ownerId, long targetId);

    /// <summary>Every alias the owner has set (their whole personal nickname map).</summary>
    Task<List<UserNickname>> GetByOwnerAsync(long ownerId);

    Task AddAsync(UserNickname nickname);

    void Remove(UserNickname nickname);

    Task SaveChangesAsync();
}
