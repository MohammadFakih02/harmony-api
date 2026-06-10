using Microsoft.AspNetCore.SignalR;

namespace Harmony.API.Filters;

/// <summary>
/// Catches unhandled, unexpected system exceptions thrown during SignalR Hub invocations.
/// Writes critical error logs with stack traces and returns a generic safe message to clients.
/// </summary>
public class HubExceptionFilter : IHubFilter
{
    private readonly ILogger<HubExceptionFilter> _logger;

    public HubExceptionFilter(ILogger<HubExceptionFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next
    )
    {
        try
        {
            return await next(invocationContext);
        }
        catch (Exception ex)
        {
            // Unexpected infrastructure or framework crash
            _logger.LogError(
                ex,
                "Hub invocation critical unhandled error in method: {Method}",
                invocationContext.HubMethodName
            );

            // Mask raw exception details to prevent leaking database configurations to the public client
            throw new HubException(
                "An unexpected real-time server error occurred. Please try again."
            );
        }
    }
}
