using System;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Harmony.Core.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

            // 401 Unauthorized (Authentication failures)
            AuthenticationException => (StatusCodes.Status401Unauthorized, "Unauthorized"),

            // 403 Forbidden (Permission/Access failures)
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden Access"),

            // 503 Service Unavailable (Circuit Breaker open / Outages)
            ServiceUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable"
            ),

            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
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
