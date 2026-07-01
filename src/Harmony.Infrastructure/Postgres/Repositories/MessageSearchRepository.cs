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

    public async Task<List<MessageSearch>> SearchAsync(
        long guildId,
        string query,
        long? channelId,
        long? before,
        int limit,
        CancellationToken ct = default
    )
    {
        // content_search is a GENERATED tsvector column (deliberately unmapped — see MessageSearch),
        // so we query it with raw SQL. Everything interpolated below is a *parameter*, not string
        // concatenation: plainto_tsquery treats the user's query as data (no FTS/SQL injection), and
        // guildId/channelId/before/limit are bound too. channelId/before are optional — sentinels
        // (0 / long.MaxValue) keep a single prepared statement shape (channel ids are snowflakes > 0,
        // created_at is unix-ms < long.MaxValue, so neither sentinel can collide with a real row).
        var channelFilter = channelId ?? 0;
        var beforeCursor = before ?? long.MaxValue;

        return await _db
            .MessagesSearch.FromSqlInterpolated(
                $@"
                SELECT message_id, channel_id, guild_id, user_id, content, created_at
                FROM ""MessagesSearch""
                WHERE guild_id = {guildId}
                  AND content_search @@ plainto_tsquery('english', {query})
                  AND ({channelFilter} = 0 OR channel_id = {channelFilter})
                  AND created_at < {beforeCursor}
                ORDER BY created_at DESC
                LIMIT {limit}"
            )
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
