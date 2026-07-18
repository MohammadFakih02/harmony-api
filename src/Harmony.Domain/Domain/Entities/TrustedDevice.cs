namespace Harmony.Domain.Domain.Entities;

/// <summary>
/// A "remember this device for 30 days" grant — mirrors <see cref="RefreshToken"/>: the raw random
/// token lives in an httpOnly cookie on the client, only its SHA-256 hash is stored here. A valid,
/// unexpired match lets <c>LoginAsync</c> skip the email-code 2FA challenge for that device.
/// </summary>
public class TrustedDevice
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public long CreatedAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    // Navigation
    public User User { get; set; } = null!;
}
