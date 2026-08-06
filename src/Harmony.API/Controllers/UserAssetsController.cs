using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Profile-asset (avatar/banner) uploads — the user-scoped presign flow. Same two-step pipeline as
/// chat attachments (NON-NEGOTIABLE #5 — the API never touches file bytes, only presigned URLs);
/// ownership is inherent (everything here operates on the caller's own profile). The confirmed
/// asset is served publicly through <see cref="PublicFilesController"/>.
/// </summary>
[ApiController]
[Route("api/users/me/{kind:regex(^(avatar|banner)$)}")]
[Authorize]
[EnableRateLimiting("api")]
public class UserAssetsController : HarmonyControllerBase
{
    private readonly IFileService _files;

    public UserAssetsController(IFileService files)
    {
        _files = files;
    }

    // POST /api/users/me/{avatar|banner}/presign
    [HttpPost("presign")]
    public async Task<IActionResult> Presign(
        string kind,
        [FromBody] PresignFileRequest request,
        CancellationToken ct
    )
    {
        var response = await _files.PresignUserAssetAsync(GetUserId(), kind, request, ct);
        return Ok(response);
    }

    // POST /api/users/me/{avatar|banner}/{fileId}/confirm
    [HttpPost("{fileId:long}/confirm")]
    public async Task<IActionResult> Confirm(string kind, long fileId, CancellationToken ct)
    {
        var response = await _files.ConfirmUserAssetAsync(GetUserId(), kind, fileId, ct);
        return Ok(response);
    }

    // DELETE /api/users/me/{avatar|banner}
    [HttpDelete]
    public async Task<IActionResult> Remove(string kind, CancellationToken ct)
    {
        await _files.RemoveUserAssetAsync(GetUserId(), kind, ct);
        return NoContent();
    }

}
