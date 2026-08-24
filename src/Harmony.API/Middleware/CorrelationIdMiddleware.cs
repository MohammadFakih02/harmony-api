using Harmony.Application.Observability;
using Serilog.Context;

namespace Harmony.API.Middleware;

/// <summary>
/// Assigns each request a correlation id (audit A15): honours an inbound
/// <c>X-Correlation-Id</c> header if a gateway/client supplied one, otherwise uses the framework's
/// per-request <see cref="HttpContext.TraceIdentifier"/>. The id is (a) pushed into the Serilog
/// <see cref="LogContext"/> so every log line for the request carries it — including the
/// <c>UseSerilogRequestLogging</c> completion line — (b) stored in the ambient
/// <see cref="CorrelationContext"/> so the RabbitMQ publisher can stamp it onto outgoing messages, and
/// (c) echoed back on the response so a client/operator can quote it when reporting an issue.
/// </summary>
/// <remarks>
/// Register early — after <c>UseForwardedHeaders</c> (so a proxied id/IP is already resolved) and
/// before <c>UseSerilogRequestLogging</c> (so the summary line is enriched). Purely observability: it
/// never short-circuits or alters the request.
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId =
            context.Request.Headers.TryGetValue(CorrelationContext.HeaderName, out var header)
            && !string.IsNullOrWhiteSpace(header)
                ? header.ToString()
                : context.TraceIdentifier;

        CorrelationContext.Current = correlationId;
        context.Response.Headers[CorrelationContext.HeaderName] = correlationId;

        using (LogContext.PushProperty(CorrelationContext.LogProperty, correlationId))
        {
            await _next(context);
        }
    }
}
