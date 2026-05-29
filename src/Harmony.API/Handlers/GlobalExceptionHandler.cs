using System.Security.Authentication;
using Cassandra; // Reference the driver directly
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Harmony.API.Handlers;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        _logger.LogError(
            exception,
            "An unhandled exception occurred: {Message}",
            exception.Message
        );

        var (statusCode, title) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource Not Found"),
            System.Security.Authentication.AuthenticationException => (
                StatusCodes.Status401Unauthorized,
                "Unauthorized"
            ),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden Access"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),

            // Map Cassandra driver exceptions to 503 directly here [1]
            NoHostAvailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "Database Service Unavailable"
            ),
            OperationTimedOutException => (
                StatusCodes.Status503ServiceUnavailable,
                "Database Query Timed Out"
            ),

            InvalidOperationException ex when ex.Message.Contains("already") => (
                StatusCodes.Status409Conflict,
                "Conflict"
            ),
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Bad Request"),
            _ => (StatusCodes.Status500InternalServerError, "Server Error"),
        };

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = exception.Message,
            Instance = httpContext.Request.Path,
        };

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
