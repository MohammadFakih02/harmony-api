using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;

namespace Harmony.Application.Services;

/// <summary>
/// Guild-scoped full-text search. The repository does the FTS + keyset ordering; this layer enforces
/// the two things the query can't: the caller must be a guild member, and every hit must live in a
/// channel the caller can <c>ViewChannel</c> (applying overrides — the same cached resolve the unread
/// fan-out uses). Because visibility filtering can drop rows, we over-fetch and trim to a page.
/// </summary>
public class SearchService : ISearchService
{
    private const int PageSize = 25;

    // Fetch several pages' worth of raw hits so override-hidden results don't starve a page. Bounded
    // so a query in a heavily-restricted guild can't scan unboundedly.
    private const int FetchLimit = PageSize * 4 + 1;

    private readonly IMessageSearchRepository _search;
    private readonly IGuildRepository _guilds;
    private readonly IChannelRepository _channels;
    private readonly IUserRepository _users;
    private readonly IPermissionService _permissions;
    private readonly IDirectMessageRepository _dms;

    public SearchService(
        IMessageSearchRepository search,
        IGuildRepository guilds,
        IChannelRepository channels,
        IUserRepository users,
        IPermissionService permissions,
        IDirectMessageRepository dms
    )
    {
        _search = search;
        _guilds = guilds;
        _channels = channels;
        _users = users;
        _permissions = permissions;
        _dms = dms;
    }

    public async Task<SearchResultsResponse> SearchGuildAsync(
        long userId,
        long guildId,
        string query,
        long? channelId,
        long? before,
        CancellationToken ct = default
    )
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return new SearchResultsResponse([], HasMore: false);

        if (!await _guilds.IsMemberAsync(guildId, userId))
            throw new UnauthorizedAccessException("You are not a member of this guild.");

        var raw = await _search.SearchAsync(guildId, query, channelId, before, FetchLimit, ct);

        // Keep hits in channels the caller can view. Cache the per-channel decision within the request
        // (many hits share a channel); IPermissionService also caches in Redis across requests.
        var canView = new Dictionary<long, bool>();
        var visible = new List<MessageSearch>(PageSize + 1);
        foreach (var row in raw)
        {
            if (!canView.TryGetValue(row.ChannelId, out var ok))
            {
                ok = await _permissions.HasAsync(
                    userId,
                    guildId,
                    Permission.ViewChannel,
                    row.ChannelId,
                    ct
                );
                canView[row.ChannelId] = ok;
            }

            if (!ok)
                continue;

            visible.Add(row);
            if (visible.Count > PageSize)
                break; // we now hold a full page + 1 → enough to know there's more
        }

        // More to load if we found a visible row past the page, or the raw fetch itself hit its cap
        // (there may be further matching rows we didn't scan). Conservative but never under-reports.
        var hasMore = visible.Count > PageSize || raw.Count == FetchLimit;
        var page = visible.Count > PageSize ? visible.GetRange(0, PageSize) : visible;

        var users = await _users.GetByIdsAsync(page.Select(r => r.UserId).Distinct());

        var channelNames = new Dictionary<long, string>();
        foreach (var cid in page.Select(r => r.ChannelId).Distinct())
        {
            var channel = await _channels.GetByIdAsync(cid);
            channelNames[cid] = channel?.Name ?? "unknown";
        }

        var results = page.Select(r =>
            {
                users.TryGetValue(r.UserId, out var user);
                return new SearchResultResponse(
                    MessageId: r.MessageId,
                    ChannelId: r.ChannelId,
                    ChannelName: channelNames.GetValueOrDefault(r.ChannelId, "unknown"),
                    GuildId: r.GuildId,
                    UserId: r.UserId,
                    Username: user?.UserName ?? "Unknown",
                    AvatarKey: user?.AvatarKey,
                    Content: r.Content,
                    CreatedAt: r.CreatedAt
                );
            })
            .ToList();

        return new SearchResultsResponse(results, hasMore);
    }

    public async Task<SearchResultsResponse> SearchDmChannelAsync(
        long userId,
        long channelId,
        string query,
        long? before,
        CancellationToken ct = default
    )
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return new SearchResultsResponse([], HasMore: false);

        // Participation IS the authorization — a guild channel has no dm_participants row, so this
        // also rejects any attempt to search a guild channel through the DM endpoint.
        if (!await _dms.IsParticipantAsync(channelId, userId))
            throw new UnauthorizedAccessException("You are not a participant of this conversation.");

        // Single channel, no per-row visibility filtering — fetch exactly one page + 1 to know if
        // there's more.
        var raw = await _search.SearchChannelAsync(channelId, query, before, PageSize + 1, ct);
        var hasMore = raw.Count > PageSize;
        var page = hasMore ? raw.GetRange(0, PageSize) : raw;

        var users = await _users.GetByIdsAsync(page.Select(r => r.UserId).Distinct());
        var channel = await _channels.GetByIdAsync(channelId);
        var channelName = channel?.Name ?? "Direct Message";

        var results = page.Select(r =>
            {
                users.TryGetValue(r.UserId, out var user);
                return new SearchResultResponse(
                    MessageId: r.MessageId,
                    ChannelId: r.ChannelId,
                    ChannelName: channelName,
                    GuildId: r.GuildId,
                    UserId: r.UserId,
                    Username: user?.UserName ?? "Unknown",
                    AvatarKey: user?.AvatarKey,
                    Content: r.Content,
                    CreatedAt: r.CreatedAt
                );
            })
            .ToList();

        return new SearchResultsResponse(results, hasMore);
    }
}
