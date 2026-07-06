using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class FriendRepository : IFriendRepository
{
    private const string Accepted = "accepted";
    private const string Pending = "pending";

    private readonly HarmonyDbContext _db;

    public FriendRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<Friend?> GetBetweenAsync(long userA, long userB) =>
        await _db.Friends.FirstOrDefaultAsync(f =>
            (f.RequesterId == userA && f.AddresseeId == userB)
            || (f.RequesterId == userB && f.AddresseeId == userA)
        );

    public async Task<List<long>> GetFriendIdsAsync(long userId) =>
        await _db
            .Friends.Where(f =>
                f.Status == Accepted
                && (f.RequesterId == userId || f.AddresseeId == userId)
            )
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
            .ToListAsync();

    public async Task<List<Friend>> GetAcceptedAsync(long userId) =>
        await _db
            .Friends.AsNoTracking()
            .Where(f =>
                f.Status == Accepted
                && (f.RequesterId == userId || f.AddresseeId == userId)
            )
            .OrderByDescending(f => f.UpdatedAt)
            .ToListAsync();

    public async Task<List<Friend>> GetPendingForAsync(long userId) =>
        await _db
            .Friends.AsNoTracking()
            .Where(f =>
                f.Status == Pending
                && (f.RequesterId == userId || f.AddresseeId == userId)
            )
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();

    public async Task AddAsync(Friend friend) => await _db.Friends.AddAsync(friend);

    public void Remove(Friend friend) => _db.Friends.Remove(friend);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
