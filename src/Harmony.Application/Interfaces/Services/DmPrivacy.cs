namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Who may open a <em>new</em> DM (or be added to a group DM) with a user. Stored as a
/// comma-separated set of audience tokens (<see cref="Everyone"/> / <see cref="Friends"/> /
/// <see cref="GuildMembers"/>) rather than a single enum value, so it's a checklist, not a
/// radio button — e.g. "friends,guild_members" allows both without allowing strangers. Adding a
/// future audience (e.g. a specific guild) is just a new token here plus a new predicate branch
/// in <see cref="CanReceiveFrom"/>; every enforcement call site stays untouched.
///
/// <see cref="Everyone"/> short-circuits: if present, every other token is redundant (checked =
/// allow anyone). An empty set means "no new contact" — existing conversations are exempt from
/// this check entirely, everywhere it's enforced.
/// </summary>
public static class DmPrivacy
{
    public const string Everyone = "everyone";
    public const string Friends = "friends";
    public const string GuildMembers = "guild_members";

    public static readonly IReadOnlySet<string> AllowedTokens = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        Everyone,
        Friends,
        GuildMembers,
    };

    /// <summary>The default for a newly-registered user — unrestricted.</summary>
    public const string Default = Everyone;

    /// <summary>The pre-checklist single enum value this replaces — mapped to <see cref="Friends"/>
    /// on read so existing rows keep their meaning without a data migration.</summary>
    private const string LegacyFriendsOnly = "friends_only";

    public static IReadOnlySet<string> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return new HashSet<string>();
        var tokens = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t == LegacyFriendsOnly ? Friends : t);
        return tokens.ToHashSet(StringComparer.Ordinal);
    }

    public static bool IsValidSet(string? csv) => Parse(csv).All(AllowedTokens.Contains);

    public static string Normalize(IEnumerable<string> tokens)
    {
        var set = tokens.Where(AllowedTokens.Contains).ToHashSet(StringComparer.Ordinal);
        // Everyone subsumes every other token — store it alone so a stray "friends" alongside it
        // can't be misread as a narrower intent by a future reader of the raw column.
        if (set.Contains(Everyone))
            return Everyone;
        return string.Join(',', set.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// The single enforcement predicate every DM-open/group-add/send call site shares. Answers
    /// "may a new sender who is/isn't a friend and does/doesn't share a guild with the target
    /// start contacting them?" — existing conversations are never routed through this check.
    /// </summary>
    public static bool CanReceiveFrom(string? targetPrivacyCsv, bool isFriend, bool sharesGuild)
    {
        var tokens = Parse(targetPrivacyCsv);
        if (tokens.Contains(Everyone))
            return true;
        if (isFriend && tokens.Contains(Friends))
            return true;
        if (sharesGuild && tokens.Contains(GuildMembers))
            return true;
        return false;
    }
}
