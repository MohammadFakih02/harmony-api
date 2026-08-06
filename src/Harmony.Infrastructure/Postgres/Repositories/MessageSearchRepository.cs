using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class MessageSearchRepository : IMessageSearchRepository
{
    private readonly HarmonyDbContext _db;

    public MessageSearchRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    // A raw ILIKE substring pattern for the user's query, with the LIKE metacharacters escaped so
    // they match literally (default '\' escape). This is the fallback that makes the search behave
    // like users expect: plainto_tsquery('english', …) drops English stop-words ("how are you" →
    // empty query → zero hits) and pure punctuation ("#" → empty), and only matches whole stemmed
    // lexemes (no substrings). ILIKE '%…%' catches all of those cases. The tsquery clause stays as
    // the fast, index-backed path; ILIKE is OR'd in as a correctness backstop (a sequential scan
    // over the already guild-/channel-narrowed rows — fine at current scale).
    private static string ToLikePattern(string query) =>
        "%"
        + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")
        + "%";

    public async Task<List<MessageSearch>> SearchAsync(
        long guildId,
        string query,
        long? channelId,
        long? authorId,
        long? after,
        bool hasLink,
        long? before,
        int limit,
        CancellationToken ct = default
    )
    {
        // content_search is a GENERATED tsvector column (deliberately unmapped — see MessageSearch),
        // so we query it with raw SQL. Everything interpolated below is a *parameter*, not string
        // concatenation: plainto_tsquery treats the user's query as data (no FTS/SQL injection), and
        // guildId/channelId/authorId/before/after/limit are bound too. The optional filters use
        // sentinels (0 / long.MaxValue) so the statement keeps a single prepared shape: channel/user
        // ids are snowflakes > 0 and created_at is unix-ms in (0, long.MaxValue), so no sentinel can
        // collide with a real row. `hasLink` is a bound bool that toggles a constant regex clause —
        // `https?://` is a literal, never user input.
        var channelFilter = channelId ?? 0;
        var authorFilter = authorId ?? 0;
        var afterCursor = after ?? 0; // created_at > 0 matches everything (0 = "no lower bound")
        var beforeCursor = before ?? long.MaxValue;
        // An operators-only query ("from:alice") has empty free text — the tsquery/ILIKE pair would
        // then match nothing, so treat empty text as "match any" and let the filters do the narrowing.
        var textFilter = query.Length == 0;
        var likePattern = ToLikePattern(query);

        return await _db
            .MessagesSearch.FromSqlInterpolated(
                $@"
                SELECT message_id, channel_id, guild_id, user_id, content, created_at
                FROM ""MessagesSearch""
                WHERE guild_id = {guildId}
                  AND ({textFilter}
                       OR content_search @@ plainto_tsquery('english', {query})
                       OR content ILIKE {likePattern})
                  AND ({channelFilter} = 0 OR channel_id = {channelFilter})
                  AND ({authorFilter} = 0 OR user_id = {authorFilter})
                  AND ({hasLink} = FALSE OR content ~* 'https?://')
                  AND created_at > {afterCursor}
                  AND created_at < {beforeCursor}
                ORDER BY created_at DESC
                LIMIT {limit}"
            )
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<List<MessageSearch>> SearchChannelAsync(
        long channelId,
        string query,
        long? authorId,
        long? after,
        bool hasLink,
        long? before,
        int limit,
        CancellationToken ct = default
    )
    {
        // Single-channel FTS — the channel id is the whole scope (a DM has no guild, and a guild
        // channel's visibility is enforced above), so there is no per-row visibility filtering. Same
        // parameterization discipline as SearchAsync: the query is data, never concatenated SQL.
        var authorFilter = authorId ?? 0;
        var afterCursor = after ?? 0;
        var beforeCursor = before ?? long.MaxValue;
        var textFilter = query.Length == 0;
        var likePattern = ToLikePattern(query);

        return await _db
            .MessagesSearch.FromSqlInterpolated(
                $@"
                SELECT message_id, channel_id, guild_id, user_id, content, created_at
                FROM ""MessagesSearch""
                WHERE channel_id = {channelId}
                  AND ({textFilter}
                       OR content_search @@ plainto_tsquery('english', {query})
                       OR content ILIKE {likePattern})
                  AND ({authorFilter} = 0 OR user_id = {authorFilter})
                  AND ({hasLink} = FALSE OR content ~* 'https?://')
                  AND created_at > {afterCursor}
                  AND created_at < {beforeCursor}
                ORDER BY created_at DESC
                LIMIT {limit}"
            )
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
