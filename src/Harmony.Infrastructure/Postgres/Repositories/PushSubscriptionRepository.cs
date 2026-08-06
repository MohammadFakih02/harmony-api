using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class PushSubscriptionRepository : IPushSubscriptionRepository
{
    private readonly HarmonyDbContext _db;

    public PushSubscriptionRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    // Tracked: the upsert path mutates the row (reassign owner / refresh keys).
    public async Task<UserPushSubscription?> GetByEndpointAsync(string endpoint) =>
        await _db.UserPushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint);

    // Tracked (not AsNoTracking): the dispatcher prunes Gone subscriptions from this list.
    public async Task<List<UserPushSubscription>> GetForUserAsync(long userId) =>
        await _db.UserPushSubscriptions.Where(s => s.UserId == userId).ToListAsync();

    public async Task AddAsync(UserPushSubscription subscription) =>
        await _db.UserPushSubscriptions.AddAsync(subscription);

    public void Remove(UserPushSubscription subscription) =>
        _db.UserPushSubscriptions.Remove(subscription);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
