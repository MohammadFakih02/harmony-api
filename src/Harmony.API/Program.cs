using System.Security.Claims;
using System.Text.Json;
using Harmony.API.Extensions;
using Harmony.API.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.RabbitMQ;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serilog;

// -----------------------------------------------------------------------
// Bootstrap logger — active only until AddHarmonySerilog below hands control to the
// fully-configured host logger. Exists so a crash during configuration (before DI/config
// are up) still gets logged instead of silently vanishing.
// -----------------------------------------------------------------------
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    Log.Information("Starting Harmony API");

    var builder = WebApplication.CreateBuilder(args);

    // === Logging ===========================================================
    // Serilog replaces the default logging provider; sink/format chosen per environment.
    builder.AddHarmonySerilog();

    // === Fail-fast configuration validation (audit A14) ====================
    // appsettings.json ships required secrets as empty placeholders (real values arrive from env vars /
    // user-secrets). Because the keys exist as "", consumers would otherwise boot with an empty JWT
    // signing key / broken storage instead of throwing. Refuse to start a production-like instance that
    // is missing one — no-op in Development/Test (they run with placeholders by design).
    builder.Configuration.ValidateRequiredConfiguration(builder.Environment);

    // Rate limiting defaults ON — an unset config key must never mean "unprotected" (NON-NEGOTIABLE #7).
    // Computed here (not just in AddHarmonyRateLimiting) because the same flag also gates UseRateLimiter
    // in the pipeline below. Test stays hard-gated off regardless of config. The flag also gates the
    // SignalR hub limiter inside AddInfrastructureServices — that one is a separate Redis-backed
    // IHubFilter, because middleware only ever sees the negotiate request and never the WebSocket frames
    // that carry SendMessage.
    var rateLimitingEnabled =
        !builder.Environment.IsEnvironment("Test")
        && builder.Configuration.GetValue("RateLimiting:Enabled", true);

    // === Service registration ==============================================
    builder
        .Services.AddHarmonySnowflake(builder.Configuration)
        .AddHarmonyForwardedHeaders(builder.Environment)
        .AddHarmonyCors(builder.Configuration)
        .AddHarmonyResponseCompression()
        .AddHarmonyIdentityAndJwt(builder.Configuration);

    if (rateLimitingEnabled)
        builder.Services.AddHarmonyRateLimiting();

    // Infrastructure (Postgres, Scylla, RabbitMQ, Redis, SignalR, repositories, services, consumers,
    // background workers, resilience, health checks) — see DependencyInjection.cs for the full map.
    builder.Services.AddInfrastructureServices(builder.Configuration, builder.Environment);

    // HubBroadcaster is registered here (API layer), not in AddInfrastructureServices, because it holds
    // IHubContext<ChatHub, IChatClient> which requires ChatHub to be known. Infrastructure depends on
    // the IHubBroadcaster abstraction (in Application). Singleton so the singleton ScyllaMessageConsumer
    // can inject it.
    builder.Services.AddSingleton<IHubBroadcaster, HubBroadcaster>();

    // The MVC/API surface: exception handling, validation, controller filters, OpenAPI.
    builder.Services.AddHarmonyWebApi();

    // Orleans silo (Track D0) — co-hosted in this same process, gated out of Test.
    builder.AddHarmonyOrleans();

    // =======================================================================
    var app = builder.Build();
    // =======================================================================

    // Apply pending EF Core migrations at startup ONLY when explicitly opted in — the container stack
    // sets RunMigrationsOnStartup=true so a fresh `docker compose up` provisions the Postgres schema
    // with no manual `dotnet ef database update`. Off by default (unset / false), so local development
    // and the test host keep their existing workflow and this block is a no-op for them. Scylla,
    // object-storage bucket, and RabbitMQ topology already self-provision on first use.
    if (app.Configuration.GetValue<bool>("RunMigrationsOnStartup"))
    {
        using var migrationScope = app.Services.CreateScope();
        migrationScope.ServiceProvider.GetRequiredService<HarmonyDbContext>().Database.Migrate();
    }

    // Development only — the deployed environment exposes neither the spec nor the UI, so the full
    // endpoint surface is never published. Swashbuckle is used purely as a UI shell over the document
    // Microsoft.AspNetCore.OpenApi generates; there is no AddSwaggerGen and no second spec generator.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "Harmony API v1");
            options.RoutePrefix = "docs";
            options.DocumentTitle = "Harmony API";
        });
    }

    // Trigger RabbitMQ connection and topology declaration asynchronously without blocking. Simply
    // opening and disposing a channel triggers the lazy asynchronous connection and topology
    // declaration cleanly without blocking the startup thread pool.
    var rabbitConnection = app.Services.GetRequiredService<RabbitMQConnection>();
    await using (var startupChannel = await rabbitConnection.CreateChannelAsync()) { }

    // === Middleware pipeline (order is load-bearing) =======================
    app.UseForwardedHeaders();

    // Assign/propagate a correlation id before the request-logging line so every log event for the
    // request — and any RabbitMQ message it publishes — is groupable by the same id (audit A15).
    app.UseMiddleware<Harmony.API.Middleware.CorrelationIdMiddleware>();

    // One structured line per request (method, path, status, elapsed ms). Placed right after
    // forwarded-headers so the client IP it can enrich with is already resolved, and before every
    // other branch so it wraps the whole pipeline, 404s and exceptions included. UserId is attached
    // via the same claim lookup ChatHub/PermissionAuthorizationFilter use — populated by the time this
    // runs regardless of pipeline order, since it fires after the request completes.
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            var userId = (
                httpContext.User.FindFirst(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirst("sub")
            )?.Value;
            if (userId is not null)
            {
                diagnosticContext.Set("UserId", userId);
            }
        };
    });

    // Compression wraps every downstream body writer, so it goes early — but NOT around two branches:
    //
    //   /api/auth — a login/register/refresh response embeds a freshly-minted JWT alongside text the
    //     caller controls (the username it echoes back). That is the exact shape BREACH needs: vary the
    //     controlled text, watch the compressed length, and recover the secret byte by byte. These
    //     responses are a few hundred bytes; there is nothing to win by compressing them. Excluding the
    //     branch outright is stronger than IHttpsCompressionFeature's DoNotCompress, which the
    //     middleware only consults for HTTPS requests and would therefore skip on plain-HTTP dev traffic.
    //
    //   /hubs — SignalR. WebSocket frames never pass through this middleware anyway, negotiate is far
    //     too small to benefit, and the SSE/long-polling fallbacks stream their bodies, which a
    //     buffering compressor would stall.
    //
    // UseWhen (not MapWhen) so the branch rejoins the main pipeline.
    app.UseWhen(
        ctx =>
            !ctx.Request.Path.StartsWithSegments("/api/auth")
            && !ctx.Request.Path.StartsWithSegments("/hubs"),
        branch => branch.UseResponseCompression()
    );

    app.UseCors("HarmonyClient");
    app.UseAuthentication();
    app.UseAuthorization();
    if (!app.Environment.IsEnvironment("Test"))
    {
        app.UseHttpsRedirection();
    }
    if (rateLimitingEnabled)
    {
        app.UseRateLimiter();
    }
    app.UseExceptionHandler();

    app.MapControllers();
    app.MapHub<ChatHub>("/hubs/chat");

    // === Health ============================================================
    // Single comprehensive endpoint (Postgres/Redis/Scylla/RabbitMQ + DLQ depth, registered in
    // DependencyInjection.AddInfrastructureServices). Anonymous, unrate-limited, uncompressed (payload
    // carries no secret to protect from BREACH). Default HealthCheckOptions.ResultStatusCodes already
    // maps Healthy/Degraded -> 200 and Unhealthy -> 503, so a Degraded Redis or a non-empty DLQ shows up
    // in the payload for ops without pulling the task out of ALB rotation — only a genuinely down core
    // dependency (Postgres/Scylla/RabbitMQ) does that.
    app.MapHealthChecks(
        "/health",
        new HealthCheckOptions
        {
            ResponseWriter = async (httpContext, report) =>
            {
                httpContext.Response.ContentType = "application/json";
                var payload = new
                {
                    status = report.Status.ToString(),
                    totalDurationMs = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        durationMs = e.Value.Duration.TotalMilliseconds,
                        data = e.Value.Data.Count > 0 ? e.Value.Data : null,
                    }),
                };
                await httpContext.Response.WriteAsync(JsonSerializer.Serialize(payload));
            },
        }
    );

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException is thrown by `dotnet ef` design-time host builds (EF spins up the host
    // just far enough to read DI-registered DbContext config, then aborts on purpose) — logging
    // that as fatal would turn every `dotnet ef migrations add` into a scary false alarm.
    Log.Fatal(ex, "Harmony API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
