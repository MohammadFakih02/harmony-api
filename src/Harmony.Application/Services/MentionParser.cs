using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Harmony.Application.Services;

/// <summary>
/// Pure, server-side `@mention` tokenizer. Captures the maximal run of ASP.NET Identity's
/// default allowed username characters after `@` and matches it EXACTLY (case-insensitive)
/// against a caller-supplied candidate dictionary — no fuzzy/prefix matching, since the
/// regex already captures the maximal valid run. `@everyone`/`@here` are recognized as
/// literal tokens only when `guildContext` is true (a DM has no guild to address).
/// </summary>
public static class MentionParser
{
    private static readonly Regex TokenRegex = new(@"@([A-Za-z0-9\-._+]+)", RegexOptions.Compiled);

    public record ParsedMentions(HashSet<long> UserIds, bool Everyone, bool Here);

    public static ParsedMentions Parse(
        string content,
        IReadOnlyDictionary<string, long> usersByUsernameLower,
        bool guildContext
    )
    {
        var ids = new HashSet<long>();
        var everyone = false;
        var here = false;

        foreach (Match match in TokenRegex.Matches(content))
        {
            var token = match.Groups[1].Value;

            if (guildContext && token.Equals("everyone", System.StringComparison.OrdinalIgnoreCase))
            {
                everyone = true;
                continue;
            }

            if (guildContext && token.Equals("here", System.StringComparison.OrdinalIgnoreCase))
            {
                here = true;
                continue;
            }

            if (usersByUsernameLower.TryGetValue(token.ToLowerInvariant(), out var userId))
                ids.Add(userId);
        }

        return new ParsedMentions(ids, everyone, here);
    }
}
