using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Harmony.API.Extensions;
using Harmony.API.Filters;
using Harmony.API.Handlers;
using Harmony.API.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Application.Validation;
using Harmony.Domain.Domain.Entities;
using Harmony.Infrastructure.Extensions;
using Harmony.Infrastructure.Postgres;
using Harmony.Infrastructure.RabbitMQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Formatting.Compact;

// -----------------------------------------------------------------------
// Bootstrap logger — active only until UseSerilog below hands control to the
// fully-configured host logger. Exists so a crash during configuration
// (before DI/config are up) still gets logged instead of silently vanishing.
// -----------------------------------------------------------------------
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    Log.Information("Starting Harmony API");

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------
// Serilog — replaces the default Microsoft.Extensions.Logging console
// provider entirely (UseSerilog's default writeToProviders: false).
//
// Sink/format is chosen in code, not config: Test stays quiet (mirrors the
// old HarmonyWebApplicationFactory Logging:LogLevel overrides — an
// integration run fires hundreds of requests), Development gets a
// human-readable line, everything else gets one-line JSON. JSON in
// non-dev is deliberate: ECS/Fargate ships container stdout straight to
// CloudWatch Logs (§20), so a JSON line becomes one directly-queryable log
// event with no separate shipper to stand up.
//
// MinimumLevel/overrides ARE config-driven (the "Serilog" section below),
// so a deployment can turn up verbosity via an env var with no rebuild —
// same pattern as RateLimiting:Enabled / Cors:AllowedOrigins.
// -----------------------------------------------------------------------
builder.Host.UseSerilog(
    (context, services, loggerConfig) =>
    {
        var env = context.HostingEnvironment;

        loggerConfig
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Harmony.API")
            .Enrich.WithProperty("Environment", env.EnvironmentName);

        if (env.IsEnvironment("Test"))
        {
            loggerConfig.MinimumLevel.Warning();
            loggerConfig.WriteTo.Console();
        }
        else if (env.IsDevelopment())
        {
            loggerConfig.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}"
            );
        }
        else
        {
            loggerConfig.WriteTo.Console(new CompactJsonFormatter());
        }
    }
);

// -----------------------------------------------------------------------
// Snowflake ID generator
// -----------------------------------------------------------------------
var workerId = builder.Configuration.GetValue<long>("Snowflake:WorkerId", 0);
var datacenterId = builder.Configuration.GetValue<long>("Snowflake:DatacenterId", 0);
builder.Services.AddSingleton<ISnowflakeIdGenerator>(_ => new SnowflakeIdGenerator(
    workerId,
    datacenterId
));

// -----------------------------------------------------------------------
// Forwarded headers
// -----------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test"))
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

// -----------------------------------------------------------------------
// CORS
//
// Origins come from config so a deployment can point at its real client host without a rebuild
// (Cors__AllowedOrigins__0=https://app.example.com as an env var, per §20's ECS/CloudFront split
// where the SPA is served from a different origin than the API). The localhost fallback keeps a
// fresh checkout working with no config. Trailing slashes are trimmed because WithOrigins compares
// origins exactly — "http://x:4200/" would silently never match.
//
// AllowAnyOrigin is deliberately NOT an option here: AllowCredentials (required for the SignalR
// WebSocket handshake) is incompatible with a wildcard origin.
// -----------------------------------------------------------------------
var corsOrigins = (
    builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4200"]
)
    .Select(o => o.TrimEnd('/'))
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "HarmonyClient",
        policy => policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
    );
});

// -----------------------------------------------------------------------
// Response compression
//
// The JSON this API returns is highly compressible and the payloads that dominate a session are
// the big ones — a message page, a member list, a guild bootstrap — so this is the cheapest
// bandwidth win available. application/json is already in ResponseCompressionDefaults.MimeTypes;
// problem+json (every error from GlobalExceptionHandler) is not, so it's added.
//
// EnableForHttps is on. That default exists because compressing a response that mixes a secret
// with attacker-influenced text leaks the secret's length (BREACH/CRIME) — so the endpoints where
// that shape actually occurs are excluded from the middleware entirely, below.
//
// CompressionLevel.Fastest is not a minor tuning knob: .NET maps Brotli's Optimal to quality 11,
// which is built for compress-once-serve-many static assets and is orders of magnitude too slow to
// sit on a dynamic response path. Fastest (quality 1) gets most of the ratio for a small fraction
// of the CPU.
// -----------------------------------------------------------------------
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/problem+json"]
    );
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o =>
    o.Level = CompressionLevel.Fastest
);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// -----------------------------------------------------------------------
// Data protection + Identity
// -----------------------------------------------------------------------
builder.Services.AddDataProtection();

builder
    .Services.AddIdentityCore<User>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<Harmony.Infrastructure.Postgres.HarmonyDbContext>()
    .AddDefaultTokenProviders();

// -----------------------------------------------------------------------
// JWT — query-string token extraction for WebSocket / SignalR
// -----------------------------------------------------------------------
builder
    .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)
            ),
            ClockSkew = TimeSpan.Zero,
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// -----------------------------------------------------------------------
// Rate limiting
//
// Defaults ON — an unset config key must never mean "unprotected" (NON-NEGOTIABLE #7). The flag
// exists so load testing can measure the app rather than the limiter (a k6 run drives thousands of
// requests per second from ONE partition key, which every policy here is designed to reject), and
// so the limiter's own per-user partition cost can be A/B'd. Test stays hard-gated off regardless
// of config, as before.
//
// The same flag also gates the SignalR hub limiter inside AddInfrastructureServices — that one is a
// separate Redis-backed IHubFilter, because middleware only ever sees the negotiate request and
// never the WebSocket frames that carry SendMessage.
// -----------------------------------------------------------------------
var rateLimitingEnabled =
    !builder.Environment.IsEnvironment("Test")
    && builder.Configuration.GetValue("RateLimiting:Enabled", true);

if (rateLimitingEnabled)
{
    builder.Services.AddHarmonyRateLimiting();
}

// -----------------------------------------------------------------------
// Infrastructure (Postgres, Scylla, RabbitMQ, SignalR backplane, repos,
// services, consumers)
// -----------------------------------------------------------------------
builder.Services.AddInfrastructureServices(builder.Configuration, builder.Environment);

// -----------------------------------------------------------------------
// HubBroadcaster — registered here (API layer) because HubBroadcaster
// holds IHubContext{ChatHub, IChatClient} which requires ChatHub to be known.
// Infrastructure depends on IHubBroadcaster (the abstraction in Application).
// Must be singleton so ScyllaMessageConsumer (singleton) can inject it.
// -----------------------------------------------------------------------
builder.Services.AddSingleton<IHubBroadcaster, HubBroadcaster>();

// -----------------------------------------------------------------------
// Global exception handling + OpenAPI
// -----------------------------------------------------------------------
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// FluentValidation — validators discovered from the Application assembly; the global
// ValidationActionFilter runs them for every controller action argument.
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
// PermissionAuthorizationFilter enforces [RequirePermission] (runs as an authorization
// filter, before model binding/validation); ValidationActionFilter validates action args.
builder.Services.AddControllers(options =>
{
    options.Filters.Add<PermissionAuthorizationFilter>();
    options.Filters.Add<ValidationActionFilter>();
});
// The transformer declares the JWT bearer scheme so the docs UI can authorize; XML doc comments
// are picked up automatically from the generated documentation files (see src/Directory.Build.props).
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>()
);

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

// Trigger RabbitMQ connection and topology declaration asynchronously without blocking
var rabbitConnection = app.Services.GetRequiredService<RabbitMQConnection>();
await using (var startupChannel = await rabbitConnection.CreateChannelAsync())
{
    // Simply opening and disposing a channel triggers the lazy asynchronous connection
    // and topology declaration cleanly without blocking the startup thread pool.
}

app.UseForwardedHeaders();

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
            httpContext.User.FindFirst(ClaimTypes.NameIdentifier) ?? httpContext.User.FindFirst("sub")
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

// -----------------------------------------------------------------------
// Health — single comprehensive endpoint (Postgres/Redis/Scylla/RabbitMQ + DLQ depth, registered in
// DependencyInjection.AddInfrastructureServices). Anonymous, unrated-limited, uncompressed (payload
// carries no secret to protect from BREACH). Default HealthCheckOptions.ResultStatusCodes already
// maps Healthy/Degraded -> 200 and Unhealthy -> 503, so a Degraded Redis or a non-empty DLQ shows up
// in the payload for ops without pulling the task out of ALB rotation — only a genuinely down core
// dependency (Postgres/Scylla/RabbitMQ) does that.
// -----------------------------------------------------------------------
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
