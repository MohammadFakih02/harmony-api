using System.Security.Claims;
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
public class SearchController : ControllerBase
{
    private readonly ISearchService _search;

    public SearchController(ISearchService search)
    {
        _search = search;
    }

    // GET /api/guilds/{guildId}/search?q=&channelId=&before=
    [HttpGet]
    public async Task<IActionResult> Search(
        long guildId,
        [FromQuery] string? q,
        [FromQuery] long? channelId,
        [FromQuery] long? before,
        CancellationToken ct
    )
    {
        var results = await _search.SearchGuildAsync(
            GetUserId(),
            guildId,
            q ?? string.Empty,
            channelId,
            before,
            ct
        );
        return Ok(results);
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
