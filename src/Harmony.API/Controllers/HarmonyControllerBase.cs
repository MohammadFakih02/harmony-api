using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Harmony.API.Controllers;

/// <summary>
/// Base class for controllers whose endpoints require an authenticated caller. Centralizes the
/// snowflake user-id extraction that was previously duplicated in every controller.
/// </summary>
/// <remarks>
/// Controllers that need to tolerate a missing/malformed claim (e.g. endpoints reachable both
/// authenticated and anonymously) keep their own nullable <c>long? GetUserId()</c> and deliberately
/// do <b>not</b> derive from this type.
/// </remarks>
public abstract class HarmonyControllerBase : ControllerBase
{
    /// <summary>
    /// The authenticated caller's snowflake id. Throws if the request is unauthenticated — only call
    /// from endpoints guarded by <c>[Authorize]</c>.
    /// </summary>
    protected long GetUserId() =>
        long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
