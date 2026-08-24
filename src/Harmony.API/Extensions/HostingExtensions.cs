using System.IO.Compression;
using System.Text;
using FluentValidation;
using Harmony.API.Filters;
using Harmony.API.Grains;
using Harmony.API.Handlers;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Application.Validation;
using Harmony.Domain.Domain.Entities;
using Harmony.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Orleans.Hosting;
using Orleans.Serialization;
using Serilog;
using Serilog.Formatting.Compact;

namespace Harmony.API.Extensions;

/// <summary>
/// Program.cs bootstrap helpers. Each method owns one cohesive slice of host/service configuration so
/// Program.cs reads as a short, skimmable outline instead of one long script. This is purely
/// organisational: every registration here is byte-identical to the inline version it replaced, in the
/// same order of effect.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Serilog — replaces the default Microsoft.Extensions.Logging console provider entirely
    /// (UseSerilog's default writeToProviders: false).
    /// </summary>
    /// <remarks>
    /// Sink/format is chosen in code, not config: Test stays quiet (mirrors the old
    /// HarmonyWebApplicationFactory Logging:LogLevel overrides — an integration run fires hundreds of
    /// requests), Development gets a human-readable line, everything else gets one-line JSON. JSON in
    /// non-dev is deliberate: ECS/Fargate ships container stdout straight to CloudWatch Logs (§20), so a
    /// JSON line becomes one directly-queryable log event with no separate shipper to stand up.
    /// MinimumLevel/overrides ARE config-driven (the "Serilog" section), so a deployment can turn up
    /// verbosity via an env var with no rebuild — same pattern as RateLimiting:Enabled / Cors:AllowedOrigins.
    /// </remarks>
    public static WebApplicationBuilder AddHarmonySerilog(this WebApplicationBuilder builder)
    {
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

        return builder;
    }

    /// <summary>Snowflake ID generator — worker/datacenter ids from config (default 0/0).</summary>
    public static IServiceCollection AddHarmonySnowflake(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var workerId = configuration.GetValue<long>("Snowflake:WorkerId", 0);
        var datacenterId = configuration.GetValue<long>("Snowflake:DatacenterId", 0);
        services.AddSingleton<ISnowflakeIdGenerator>(_ => new SnowflakeIdGenerator(
            workerId,
            datacenterId
        ));
        return services;
    }

    /// <summary>Forwarded headers — trust X-Forwarded-For/Proto (behind nginx/ALB); no known-proxy
    /// allow-list in Dev/Test so a local run works without one.</summary>
    public static IServiceCollection AddHarmonyForwardedHeaders(
        this IServiceCollection services,
        IHostEnvironment environment
    )
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        });
        return services;
    }

    /// <summary>
    /// CORS. Origins come from config so a deployment can point at its real client host without a
    /// rebuild (Cors__AllowedOrigins__0=https://app.example.com as an env var, per §20's ECS/CloudFront
    /// split where the SPA is served from a different origin than the API). The localhost fallback keeps
    /// a fresh checkout working with no config. Trailing slashes are trimmed because WithOrigins compares
    /// origins exactly — "http://x:4200/" would silently never match.
    /// </summary>
    /// <remarks>
    /// AllowAnyOrigin is deliberately NOT an option here: AllowCredentials (required for the SignalR
    /// WebSocket handshake) is incompatible with a wildcard origin.
    /// </remarks>
    public static IServiceCollection AddHarmonyCors(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var corsOrigins = (
            configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:4200"]
        )
            .Select(o => o.TrimEnd('/'))
            .ToArray();

        services.AddCors(options =>
        {
            options.AddPolicy(
                "HarmonyClient",
                policy =>
                    policy
                        .WithOrigins(corsOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials()
            );
        });
        return services;
    }

    /// <summary>
    /// Response compression. The JSON this API returns is highly compressible and the payloads that
    /// dominate a session are the big ones — a message page, a member list, a guild bootstrap — so this
    /// is the cheapest bandwidth win available. application/json is already in
    /// ResponseCompressionDefaults.MimeTypes; problem+json (every error from GlobalExceptionHandler) is
    /// not, so it's added.
    /// </summary>
    /// <remarks>
    /// EnableForHttps is on. That default exists because compressing a response that mixes a secret with
    /// attacker-influenced text leaks the secret's length (BREACH/CRIME) — so the endpoints where that
    /// shape actually occurs are excluded from the middleware entirely (see the /api/auth + /hubs
    /// UseWhen in Program.cs). CompressionLevel.Fastest is not a minor tuning knob: .NET maps Brotli's
    /// Optimal to quality 11, which is built for compress-once-serve-many static assets and is orders of
    /// magnitude too slow to sit on a dynamic response path. Fastest (quality 1) gets most of the ratio
    /// for a small fraction of the CPU.
    /// </remarks>
    public static IServiceCollection AddHarmonyResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                ["application/problem+json"]
            );
        });
        services.Configure<BrotliCompressionProviderOptions>(o =>
            o.Level = CompressionLevel.Fastest
        );
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }

    /// <summary>
    /// Data protection + Identity + JWT bearer. The JWT events pull the access token from the
    /// query string for WebSocket/SignalR requests (browsers can't set an Authorization header on a
    /// WebSocket handshake), scoped to the /hubs path so no REST endpoint accepts a query-string token.
    /// </summary>
    public static IServiceCollection AddHarmonyIdentityAndJwt(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddDataProtection();

        services
            .AddIdentityCore<User>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<HarmonyDbContext>()
            .AddDefaultTokenProviders();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"],
                    ValidAudience = configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!)
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

        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// The MVC/API surface: global exception handling (ProblemDetails), FluentValidation discovery, the
    /// two global controller filters, and the OpenAPI document.
    /// </summary>
    /// <remarks>
    /// PermissionAuthorizationFilter enforces [RequirePermission] as an authorization filter (before
    /// model binding/validation); ValidationActionFilter runs the discovered validators for every
    /// controller action argument. The OpenAPI transformer declares the JWT bearer scheme so the docs UI
    /// can authorize; XML doc comments are picked up automatically from the generated documentation
    /// files (see src/Directory.Build.props).
    /// </remarks>
    public static IServiceCollection AddHarmonyWebApi(this IServiceCollection services)
    {
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();

        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

        services.AddControllers(options =>
        {
            options.Filters.Add<PermissionAuthorizationFilter>();
            options.Filters.Add<ValidationActionFilter>();
        });

        services.AddOpenApi(options =>
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>()
        );
        return services;
    }

    /// <summary>
    /// Orleans silo (Track D0) — co-hosted in this same process. Localhost clustering + in-memory grain
    /// storage: a single silo needs no external membership/storage provider, and the grains arriving in
    /// D1+ rebuild their state from Postgres/Scylla/Redis on activation (durability for the D2 message
    /// path comes from a Redis WAL, not grain storage), so AdoNet-on-Postgres clustering is deferred
    /// until it is actually load-bearing.
    /// </summary>
    /// <remarks>
    /// Gated OUT of the Test environment: UseLocalhostClustering binds the fixed ports 11111/30000, and
    /// the integration suite spins up many WebApplicationFactory hosts in parallel that would collide on
    /// them. No app code depends on a grain yet — D0 only stands the silo up and health-checks it — so
    /// the integration hosts run with zero Orleans involvement. Grain unit tests use Orleans'
    /// auto-porting InProcessTestCluster instead (introduced with D1's first real grain).
    /// </remarks>
    public static WebApplicationBuilder AddHarmonyOrleans(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsEnvironment("Test"))
            return builder;

        builder.Host.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering().AddMemoryGrainStorage("Default");
            // D2a: ChannelGrain.SendMessage takes a Domain MessageSentEvent as a grain-call argument.
            // Domain is Orleans-attribute-free by design, so delegate serialization + copy of the
            // Harmony.Domain.Interfaces event records to System.Text.Json (their RabbitMQ wire form).
            // Scoped to that namespace so nothing else is affected. Must match the test cluster.
            silo.Services.AddSerializer(s =>
                s.AddJsonSerializer(type =>
                    type.Namespace?.StartsWith("Harmony.Domain.Interfaces") == true
                )
            );
        });

        builder.Services.AddHealthChecks().AddCheck<OrleansSiloHealthCheck>("orleans");
        return builder;
    }
}
