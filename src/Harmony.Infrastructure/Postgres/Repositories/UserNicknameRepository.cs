using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class UserNicknameRepository : IUserNicknameRepository
{
    private readonly HarmonyDbContext _db;

    public UserNicknameRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<UserNickname?> GetAsync(long ownerId, long targetId) =>
        await _db.UserNicknames.FirstOrDefaultAsync(n =>
            n.OwnerId == ownerId && n.TargetId == targetId
        );

    public async Task<List<UserNickname>> GetByOwnerAsync(long ownerId) =>
        await _db.UserNicknames.AsNoTracking().Where(n => n.OwnerId == ownerId).ToListAsync();

    public async Task AddAsync(UserNickname nickname) => await _db.UserNicknames.AddAsync(nickname);

    public void Remove(UserNickname nickname) => _db.UserNicknames.Remove(nickname);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
