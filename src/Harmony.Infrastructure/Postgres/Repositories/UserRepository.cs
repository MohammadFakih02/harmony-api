using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class UserRepository : IUserRepository
{
    private readonly HarmonyDbContext _db;

    public UserRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<User?> GetByIdAsync(long userId) =>
        await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);

    public async Task<Dictionary<long, User>> GetByIdsAsync(IEnumerable<long> userIds)
    {
        var ids = userIds.Distinct().ToList();
        return await _db.Users
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);
    }

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
