using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface ITrustedDeviceRepository
{
    Task AddAsync(TrustedDevice device, CancellationToken ct = default);

    /// <summary>Returns the matching row only if it exists and hasn't expired.</summary>
    Task<TrustedDevice?> GetValidAsync(long userId, string tokenHash, CancellationToken ct = default);

    /// <summary>Revokes 2FA trust for every device of this user ("Require 2FA on all devices again").</summary>
    Task DeleteAllForUserAsync(long userId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
