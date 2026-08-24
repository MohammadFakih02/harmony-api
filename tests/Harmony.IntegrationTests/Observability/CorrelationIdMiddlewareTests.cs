using FluentAssertions;
using Harmony.API.Middleware;
using Harmony.Application.Observability;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Harmony.IntegrationTests.Observability;

/// <summary>
/// Verifies the correlation-id middleware (audit A15): every request gets an id that is readable from
/// the ambient <see cref="CorrelationContext"/> while the pipeline runs (this is what the RabbitMQ
/// publisher reads) and is echoed on the response, honouring an inbound header when supplied. Pure —
/// runs on a <see cref="DefaultHttpContext"/>, no host.
/// </summary>
public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task WithNoInboundHeader_UsesTraceIdentifier_SetsAmbientContext_AndEchoesHeader()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-abc" };
        string? seenDuringPipeline = null;
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            seenDuringPipeline = CorrelationContext.Current; // readable mid-pipeline, like the publisher
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        seenDuringPipeline.Should().Be("trace-abc");
        context.Response.Headers[CorrelationContext.HeaderName].ToString().Should().Be("trace-abc");
    }

    [Fact]
    public async Task WithInboundHeader_HonoursIt_ForContextAndResponse()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-xyz" };
        context.Request.Headers[CorrelationContext.HeaderName] = "client-supplied-id";
        string? seenDuringPipeline = null;
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            seenDuringPipeline = CorrelationContext.Current;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        seenDuringPipeline.Should().Be("client-supplied-id");
        context.Response.Headers[CorrelationContext.HeaderName].ToString().Should().Be("client-supplied-id");
    }
}
