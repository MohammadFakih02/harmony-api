namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical per-guild / per-channel notification levels (§5.31, roadmap E#16), shared by the
/// validator, the controller and the resolver so they never drift.
/// <list type="bullet">
/// <item><c>all</c> — notify on every message in the scope. Produced by
/// <c>NotificationService.CreateMessageNotificationsAsync</c> (Type <c>"message"</c>), which the
/// consumer runs for every guild text message against the (opt-in) set resolved to this level.</item>
/// <item><c>mentions</c> — only @mentions notify. This is the default when no setting exists.</item>
/// <item><c>nothing</c> — suppress every notification from this scope.</item>
/// </list>
/// </summary>
public static class NotificationLevel
{
    public const string All = "all";
    public const string Mentions = "mentions";
    public const string Nothing = "nothing";

    /// <summary>The effective default when a user has no setting at the channel or guild scope.</summary>
    public const string Default = Mentions;

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        All,
        Mentions,
        Nothing,
    };

    public static bool IsValid(string? level) => level is not null && Allowed.Contains(level);
}

/// <summary>
/// The two scopes a <c>NotificationSetting</c> can target. (Guild + channel only — user-level
/// silencing already lives in <c>UserMutes</c>.)
/// </summary>
public static class NotificationScope
{
    public const string Guild = "guild";
    public const string Channel = "channel";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        Guild,
        Channel,
    };

    public static bool IsValid(string? scope) => scope is not null && Allowed.Contains(scope);
}
