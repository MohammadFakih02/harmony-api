using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// File attachments for channel messages. The object store (MinIO in dev, S3 in prod) is never
/// exposed directly — clients only ever receive presigned URLs (NON-NEGOTIABLE #5).
/// </summary>
/// <remarks>
/// <para>
/// Uploading is a three-step lifecycle: <b>presign</b> returns a short-lived PUT URL and creates an
/// unconfirmed row; the client PUTs the bytes straight to storage; <b>confirm</b> validates the
/// object (size, dimensions) and flips the row to confirmed, at which point it may be attached to a
/// message. An unconfirmed row that's never confirmed is swept and its orphaned object deleted by a
/// background service — so a presign that the client abandons doesn't leak storage.
/// </para>
/// <para>
/// Nesting under the channel lets the route-driven <see cref="RequirePermissionAttribute"/> apply
/// channel-scoped <c>AttachFiles</c> on presign and <c>ViewChannel</c> on download. Confirm carries
/// no route permission because it's owner-gated in the service — only the uploader can confirm their
/// own row.
/// </para>
/// </remarks>
[ApiController]
[Route("api/guilds/{guildId:long}/channels/{channelId:long}/files")]
[Authorize]
[EnableRateLimiting("api")]
public class FilesController : HarmonyControllerBase
{
    private readonly IFileService _files;

    public FilesController(IFileService files)
    {
        _files = files;
    }

    /// <summary>
    /// Step 1 of upload: mints a presigned PUT URL and creates the (unconfirmed) attachment row. The
    /// client uploads the bytes directly to that URL, then calls <c>confirm</c>.
    /// </summary>
    /// <response code="200">Body carries the file ID and the presigned upload URL.</response>
    /// <response code="403">The caller lacks <c>AttachFiles</c> on this channel.</response>
    [HttpPost("presign")]
    [RequirePermission(Permission.AttachFiles)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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

    /// <summary>
    /// Step 2 of upload: validates the uploaded object (size and, for images, dimensions) and marks
    /// the row confirmed so it can be attached to a message. Owner-gated in the service — only the
    /// uploader can confirm their own row.
    /// </summary>
    /// <response code="200">Confirmed; body is the finalized attachment.</response>
    /// <response code="400">The uploaded object failed validation (too large, bad dimensions).</response>
    /// <response code="403">Not the uploader of this file.</response>
    [HttpPost("{fileId:long}/confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Confirm(long fileId, CancellationToken ct)
    {
        var response = await _files.ConfirmAsync(GetUserId(), fileId, ct);
        return Ok(response);
    }

    /// <summary>
    /// Mints a short-lived presigned download URL for a single attachment. Cacheable by the client
    /// for just under the URL's 15-minute lifetime.
    /// </summary>
    /// <response code="200">Body carries the presigned download URL.</response>
    /// <response code="403">The caller lacks <c>ViewChannel</c>.</response>
    [HttpGet("{fileId:long}")]
    [RequirePermission(Permission.ViewChannel)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
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

    /// <summary>
    /// Mints download URLs for many attachments at once — one round trip for a whole message page.
    /// </summary>
    /// <remarks>
    /// POST rather than GET because the ID list outgrows a query string. Unresolvable IDs are
    /// silently omitted rather than failing the batch. The batched response isn't HTTP-cacheable, but
    /// the client caches each URL per ID.
    /// </remarks>
    /// <response code="200">A map of file ID to presigned URL for every resolvable ID.</response>
    /// <response code="403">The caller lacks <c>ViewChannel</c>.</response>
    [HttpPost("batch")]
    [RequirePermission(Permission.ViewChannel)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetUrls(
        long guildId,
        long channelId,
        [FromBody] BatchFileDownloadRequest request,
        CancellationToken ct
    )
    {
        var response = await _files.GetDownloadUrlsAsync(guildId, channelId, request.FileIds, ct);
        return Ok(response);
    }

}
