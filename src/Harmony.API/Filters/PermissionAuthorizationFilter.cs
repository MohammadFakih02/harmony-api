using System.Security.Claims;
using Harmony.Application.Interfaces.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Harmony.API.Filters;

/// <summary>
/// Enforces <see cref="RequirePermissionAttribute"/> on controller actions. Reads the
/// declared permission(s) off the action's endpoint metadata, resolves the caller's
/// effective bits via <see cref="IPermissionService"/> (channel-scoped when the route
/// carries a <c>channelId</c>, so overrides apply), and short-circuits with 403 if any
/// required bit is missing. Non-members resolve to 0, so this also subsumes the membership
/// checks it replaces. Actions without the attribute pass straight through.
///
/// Registered globally (mirrors <see cref="ValidationActionFilter"/>); the scoped
/// permission service is resolved per request from <see cref="HttpContext.RequestServices"/>.
/// </summary>
public sealed class PermissionAuthorizationFilter : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var required = context
            .ActionDescriptor.EndpointMetadata.OfType<RequirePermissionAttribute>()
            .ToList();
        if (required.Count == 0)
            return;

        if (ResolveUserId(context.HttpContext.User) is not { } userId)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Permission checks are guild-scoped; without a guild in the route the attribute is
        // misapplied. Fail closed rather than silently allowing the action.
        if (!TryGetGuildId(context, out var guildId))
        {
            context.Result = Forbidden(context, "Guild context is required for this action.");
            return;
        }

        long? channelId = TryGetLong(context, "channelId", out var cid) ? cid : null;

        var permissions = context.HttpContext.RequestServices.GetRequiredService<IPermissionService>();
        var bits = await permissions.ResolveAsync(userId, guildId, channelId);

        foreach (var attr in required)
        {
            var bit = (long)attr.Permission;
            if ((bits & bit) != bit)
            {
                context.Result = Forbidden(context, $"Missing permission: {attr.Permission}.");
                return;
            }
        }
    }

    private static long? ResolveUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier) ?? user.FindFirst("sub");
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }

    private static bool TryGetGuildId(AuthorizationFilterContext context, out long guildId) =>
        TryGetLong(context, "guildId", out guildId) || TryGetLong(context, "id", out guildId);

    private static bool TryGetLong(AuthorizationFilterContext context, string key, out long value)
    {
        value = 0;
        return context.RouteData.Values.TryGetValue(key, out var raw)
            && long.TryParse(raw?.ToString(), out value);
    }

    private static ObjectResult Forbidden(AuthorizationFilterContext context, string detail) =>
        new(
            new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = detail,
                Instance = context.HttpContext.Request.Path,
            }
        )
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
}
