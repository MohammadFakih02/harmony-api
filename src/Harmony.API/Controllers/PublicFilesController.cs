using Harmony.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Serves public profile assets (avatars/banners) to bare &lt;img&gt; tags, which cannot attach a
/// JWT header — so this endpoint is anonymous by design. The service only resolves confirmed rows
/// whose key sits under avatars/ or banners/ (chat attachments can never leak through here), keys
/// are unguessable snowflake-suffixed paths, and MinIO stays unexposed: the response is a 302 to a
/// short-lived presigned GET (NON-NEGOTIABLE #5), cacheable just under the URL's lifetime.
/// </summary>
[ApiController]
[Route("api/files/public")]
[AllowAnonymous]
[EnableRateLimiting("assets")]
public class PublicFilesController : ControllerBase
{
    private readonly IFileService _files;

    public PublicFilesController(IFileService files)
    {
        _files = files;
    }

    // GET /api/files/public/{avatars|banners}/{userId}/{fileId}
    [HttpGet("{**key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        var url = await _files.GetPublicFileUrlAsync(key, ct);

        // Presigned GET lives 15 min; let browsers cache the redirect just under that.
        Response.Headers.CacheControl = "public, max-age=840";
        return Redirect(url);
    }
}
