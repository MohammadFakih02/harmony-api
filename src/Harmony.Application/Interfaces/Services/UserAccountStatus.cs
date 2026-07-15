namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical <c>User.AccountStatus</c> values, shared by every gate that has to decide whether an
/// account may still participate — so they can't drift apart on a magic string. Only
/// <see cref="Active"/> accounts can log in (AuthService) or be reached socially (friend requests,
/// new DMs).
///
/// NOTE: nothing sets a non-active status yet — the deactivate/suspend endpoints are Phase 5 work.
/// These gates exist so that feature can't ship with the social surfaces silently exempt.
/// </summary>
public static class UserAccountStatus
{
    public const string Active = "active";
    public const string Deactivated = "deactivated";
    public const string Suspended = "suspended";

    /// <summary>True only for an account that may still log in and be interacted with.</summary>
    public static bool IsActive(string? accountStatus) =>
        string.Equals(accountStatus, Active, StringComparison.Ordinal);
}
