using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Guild-asset (icon/banner) uploads — the same two-step presign pipeline as profile assets
/// (NON-NEGOTIABLE #5), ManageGuild-gated via the route filter. Confirmed assets are served
/// publicly through <see cref="PublicFilesController"/> (keys are unguessable snowflake paths).
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/{kind:regex(^(icon|banner)$)}")]
[Authorize]
[EnableRateLimiting("api")]
public class GuildAssetsController : HarmonyControllerBase
{
    private readonly IFileService _files;

    public GuildAssetsController(IFileService files)
    {
        _files = files;
    }

    // POST /api/guilds/{guildId}/{icon|banner}/presign
    [HttpPost("presign")]
    [RequirePermission(Permission.ManageGuild)]
    public async Task<IActionResult> Presign(
        long guildId,
        string kind,
        [FromBody] PresignFileRequest request,
        CancellationToken ct
    )
    {
        var response = await _files.PresignGuildAssetAsync(GetUserId(), guildId, kind, request, ct);
        return Ok(response);
    }

    // POST /api/guilds/{guildId}/{icon|banner}/{fileId}/confirm
    [HttpPost("{fileId:long}/confirm")]
    [RequirePermission(Permission.ManageGuild)]
    public async Task<IActionResult> Confirm(long guildId, string kind, long fileId, CancellationToken ct)
    {
        var response = await _files.ConfirmGuildAssetAsync(guildId, kind, fileId, ct);
        return Ok(response);
    }

    // DELETE /api/guilds/{guildId}/{icon|banner}
    [HttpDelete]
    [RequirePermission(Permission.ManageGuild)]
    public async Task<IActionResult> Remove(long guildId, string kind, CancellationToken ct)
    {
        await _files.RemoveGuildAssetAsync(guildId, kind, ct);
        return NoContent();
    }

}
