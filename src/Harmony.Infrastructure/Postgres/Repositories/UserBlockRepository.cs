using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class UserBlockRepository : IUserBlockRepository
{
    private readonly HarmonyDbContext _db;

    public UserBlockRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<UserBlock?> GetAsync(long blockerId, long blockedId) =>
        await _db.UserBlocks.FirstOrDefaultAsync(b =>
            b.BlockerId == blockerId && b.BlockedId == blockedId
        );

    public async Task<List<UserBlock>> GetByBlockerAsync(long blockerId) =>
        await _db
            .UserBlocks.Where(b => b.BlockerId == blockerId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

    public async Task<bool> AreBlockedAsync(long userA, long userB) =>
        await _db.UserBlocks.AnyAsync(b =>
            (b.BlockerId == userA && b.BlockedId == userB)
            || (b.BlockerId == userB && b.BlockedId == userA)
        );

    public async Task AddAsync(UserBlock block) => await _db.UserBlocks.AddAsync(block);

    public void Remove(UserBlock block) => _db.UserBlocks.Remove(block);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
