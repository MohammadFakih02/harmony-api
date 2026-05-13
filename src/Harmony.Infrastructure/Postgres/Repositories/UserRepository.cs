using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
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

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
