namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical DM-privacy values, shared by the validator/controller and the DM-open
/// enforcement so they never drift. Controls who may open a <em>new</em> DM with a user:
/// <see cref="Everyone"/> (anyone) or <see cref="FriendsOnly"/> (accepted friends only).
/// Existing conversations always remain reachable regardless of this setting.
/// </summary>
public static class DmPrivacy
{
    public const string Everyone = "everyone";
    public const string FriendsOnly = "friends_only";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        Everyone,
        FriendsOnly,
    };

    public static bool IsValid(string? value) => value is not null && Allowed.Contains(value);
}
