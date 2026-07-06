using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IPushSubscriptionRepository
{
    /// <summary>
    /// The subscription registered for this push-service endpoint, regardless of owner —
    /// an endpoint is device+origin-scoped, so a re-login as a different user in the same
    /// browser must find (and reassign) the existing row rather than duplicate it.
    /// </summary>
    Task<UserPushSubscription?> GetByEndpointAsync(string endpoint);

    /// <summary>All of a user's device subscriptions (read-only — the send fan-out).</summary>
    Task<List<UserPushSubscription>> GetForUserAsync(long userId);

    Task AddAsync(UserPushSubscription subscription);

    void Remove(UserPushSubscription subscription);

    Task SaveChangesAsync();
}
