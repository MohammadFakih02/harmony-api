namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical presence status values, shared by the validator, the controller, and
/// the resolution logic in RedisPresenceService so they never drift.
///
/// <para><b>Preferred</b> statuses are what a user may choose (durable). <b>Effective</b>
/// statuses are what observers see; <see cref="Invisible"/> is a preferred-only value
/// that resolves to <see cref="Offline"/> for everyone else.</para>
/// </summary>
public static class PresenceStatus
{
    public const string Online = "online";
    public const string Away = "away";
    public const string Dnd = "dnd";
    public const string Invisible = "invisible";
    public const string Offline = "offline";

    /// <summary>The set a user may set as their preferred status.</summary>
    public static readonly IReadOnlySet<string> AllowedPreferred = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        Online,
        Away,
        Dnd,
        Invisible,
    };

    public static bool IsValidPreferred(string? status) =>
        status is not null && AllowedPreferred.Contains(status);
}
