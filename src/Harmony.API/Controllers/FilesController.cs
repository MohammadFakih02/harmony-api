using System.Security.Claims;
using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// File-upload endpoints (NON-NEGOTIABLE #5 — MinIO is never exposed directly; clients only get
/// presigned URLs). Nested under the channel so the route-based <see cref="RequirePermissionAttribute"/>
/// can apply channel-scoped AttachFiles on presign. Confirm is owner-gated in the service, so it only
/// needs authentication.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/channels/{channelId:long}/files")]
[Authorize]
[EnableRateLimiting("api")]
public class FilesController : ControllerBase
{
    private readonly IFileService _files;

    public FilesController(IFileService files)
    {
        _files = files;
    }

    // POST /api/guilds/{guildId}/channels/{channelId}/files/presign
    [HttpPost("presign")]
    [RequirePermission(Permission.AttachFiles)]
    public async Task<IActionResult> Presign(
        long guildId,
        long channelId,
        [FromBody] PresignFileRequest request,
        CancellationToken ct
    )
    {
        var response = await _files.PresignAsync(GetUserId(), guildId, channelId, request, ct);
        return Ok(response);
    }

    // POST /api/guilds/{guildId}/channels/{channelId}/files/{fileId}/confirm
    [HttpPost("{fileId:long}/confirm")]
    public async Task<IActionResult> Confirm(long fileId, CancellationToken ct)
    {
        var response = await _files.ConfirmAsync(GetUserId(), fileId, ct);
        return Ok(response);
    }

    // GET /api/guilds/{guildId}/channels/{channelId}/files/{fileId}
    [HttpGet("{fileId:long}")]
    [RequirePermission(Permission.ViewChannel)]
    public async Task<IActionResult> GetUrl(
        long guildId,
        long channelId,
        long fileId,
        CancellationToken ct
    )
    {
        var response = await _files.GetDownloadUrlAsync(guildId, channelId, fileId, ct);
        // Let the client cache the presigned URL just under its 15-min lifetime.
        Response.Headers.CacheControl = "private, max-age=840";
        return Ok(response);
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
