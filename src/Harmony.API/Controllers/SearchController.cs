using Harmony.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Full-text message search within a guild (flow #26). Nested under the guild so scope is explicit;
/// there is no <c>[RequirePermission]</c> because search spans channels — membership and per-result
/// <c>ViewChannel</c> filtering (overrides included) are enforced inside <see cref="ISearchService"/>.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/search")]
[Authorize]
[EnableRateLimiting("api")]
public class SearchController : HarmonyControllerBase
{
    private readonly ISearchService _search;

    public SearchController(ISearchService search)
    {
        _search = search;
    }

    // GET /api/guilds/{guildId}/search?q=&channelId=&from=&after=&hasLink=&before=
    // The from/after/hasLink operators are additive filters (parsed client-side from `from:`/`after:`/
    // `has:link`); they only narrow within this guild — every hit still passes the service's per-row
    // ViewChannel check, so a forged id can't leak a hidden channel.
    [HttpGet]
    public async Task<IActionResult> Search(
        long guildId,
        [FromQuery] string? q,
        [FromQuery] long? channelId,
        [FromQuery] long? from,
        [FromQuery] long? after,
        [FromQuery] bool hasLink,
        [FromQuery] long? before,
        CancellationToken ct
    )
    {
        var results = await _search.SearchGuildAsync(
            GetUserId(),
            guildId,
            q ?? string.Empty,
            new SearchFilters(ChannelId: channelId, AuthorId: from, After: after, HasLink: hasLink),
            before,
            ct
        );
        return Ok(results);
    }

}
