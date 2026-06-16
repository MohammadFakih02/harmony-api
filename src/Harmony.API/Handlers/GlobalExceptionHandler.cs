using System.Security.Authentication;
using Cassandra;
using FluentValidation;
using Harmony.Application.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;

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
        if (exception is NoHostAvailableException noHostEx)
        {
            _logger.LogError("ScyllaDB connection failed. Listing raw host errors:");
            if (noHostEx.Errors != null && noHostEx.Errors.Count > 0)
            {
                foreach (var entry in noHostEx.Errors)
                {
                    var host = entry.Key?.ToString() ?? "Unknown Host";
                    var error =
                        entry.Value != null
                            ? entry.Value.ToString()
                            : "No exception recorded (host is marked DOWN)";
                    _logger.LogError("-> Host: {Host} | Error: {Error}", host, error);
                }
            }
            else
            {
                _logger.LogError(
                    "noHostEx.Errors is empty or null. The driver has marked all hosts down and skipped trying them."
                );
            }
        }

        // Dynamically adjust log level based on exception type to prevent log pollution
        if (
            exception is KeyNotFoundException
            || exception is System.Security.Authentication.AuthenticationException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is ValidationException
            || exception is InvalidOperationException
        )
        {
            _logger.LogWarning("User validation exception occurred: {Message}", exception.Message);
        }
        else
        {
            _logger.LogError(
                exception,
                "An unhandled system exception occurred: {Message}",
                exception.Message
            );
        }

        var (statusCode, title) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource Not Found"),
            System.Security.Authentication.AuthenticationException => (
                StatusCodes.Status401Unauthorized,
                "Unauthorized"
            ),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden Access"),
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),

            // Cassandra DB Driver Exceptions
            NoHostAvailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "Database Service Unavailable"
            ),
            OperationTimedOutException => (
                StatusCodes.Status503ServiceUnavailable,
                "Database Query Timed Out"
            ),

            // New Resiliency Exceptions
            ServiceUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable"
            ),
            BrokenCircuitException => (
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable (Circuit Open)"
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
