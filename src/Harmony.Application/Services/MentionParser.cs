using System;
using System.Collections.Generic;

namespace Harmony.Application.Services;

/// <summary>
/// Pure, server-side `@mention` resolver. At each `@`, it picks the <b>longest</b> candidate name
/// (user username/nickname or role name) that is a case-insensitive prefix of the following text and
/// is terminated by end-of-string or a non-username character. Matching against the actual candidate
/// set (rather than a fixed charset regex) lets multi-word nicknames and role names — which contain
/// spaces — resolve, while the terminator rule keeps the old semantics intact: `@aliceandbob` does not
/// partial-match `alice`, `@alice.com` (an email tail) matches nothing, and `@alice,` still matches
/// `alice`. `@everyone`/`@here` are recognized as literal tokens only when <paramref name="guildContext"/>
/// is true (a DM has no guild to address). User matches beat role matches on an exact-length tie.
/// </summary>
public static class MentionParser
{
    private static readonly Dictionary<string, long> Empty = new();

    public record ParsedMentions(HashSet<long> UserIds, HashSet<long> RoleIds, bool Everyone, bool Here);

    public static ParsedMentions Parse(
        string content,
        IReadOnlyDictionary<string, long> usersByNameLower,
        bool guildContext,
        IReadOnlyDictionary<string, long>? rolesByNameLower = null
    )
    {
        var userIds = new HashSet<long>();
        var roleIds = new HashSet<long>();
        var everyone = false;
        var here = false;
        rolesByNameLower ??= Empty;

        // The longest candidate bounds how far we scan after each '@'.
        var maxLen = 0;
        foreach (var k in usersByNameLower.Keys)
            if (k.Length > maxLen) maxLen = k.Length;
        foreach (var k in rolesByNameLower.Keys)
            if (k.Length > maxLen) maxLen = k.Length;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '@') continue;
            var start = i + 1;

            // @everyone / @here — recognized on the spaceless name-char run only, guild-only.
            var runEnd = start;
            while (runEnd < content.Length && IsNameChar(content[runEnd])) runEnd++;
            if (guildContext && runEnd > start)
            {
                var run = content.AsSpan(start, runEnd - start);
                if (run.Equals("everyone", StringComparison.OrdinalIgnoreCase))
                {
                    everyone = true;
                    i = runEnd - 1;
                    continue;
                }
                if (run.Equals("here", StringComparison.OrdinalIgnoreCase))
                {
                    here = true;
                    i = runEnd - 1;
                    continue;
                }
            }

            // Longest candidate that is a prefix of content[start..] AND is terminated by
            // end-of-string or a non-username char (so @John matches "John" but not "Johnson").
            var limit = Math.Min(maxLen, content.Length - start);
            for (var len = limit; len >= 1; len--)
            {
                var end = start + len;
                if (end < content.Length && IsNameChar(content[end]))
                    continue; // the candidate would run into more name chars — not a boundary

                var name = content.Substring(start, len).ToLowerInvariant();
                if (usersByNameLower.TryGetValue(name, out var uid))
                {
                    userIds.Add(uid);
                    i = end - 1;
                    break;
                }
                if (rolesByNameLower.TryGetValue(name, out var rid))
                {
                    roleIds.Add(rid);
                    i = end - 1;
                    break;
                }
            }
        }

        return new ParsedMentions(userIds, roleIds, everyone, here);
    }

    /// <summary>Characters that count as part of a plain username token (ASP.NET Identity's set).</summary>
    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '+';
}
