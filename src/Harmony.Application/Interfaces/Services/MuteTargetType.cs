namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical mute target-type values, shared by the validator and the controller so
/// they never drift. A mute silences notifications from a whole guild, a single
/// channel, or a specific user (the suppression effects are Phase 4 consumers).
/// </summary>
public static class MuteTargetType
{
    public const string Guild = "guild";
    public const string Channel = "channel";
    public const string User = "user";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        Guild,
        Channel,
        User,
    };

    public static bool IsValid(string? targetType) =>
        targetType is not null && Allowed.Contains(targetType);
}
