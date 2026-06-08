using System.Text;
using Harmony.API.Extensions;
using Harmony.API.Handlers;
using Harmony.API.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Infrastructure.Extensions;
using Harmony.Infrastructure.RabbitMQ;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

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
// -----------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "HarmonyClient",
        policy =>
            policy
                .WithOrigins("http://localhost:4200")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
    );
});

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
// -----------------------------------------------------------------------
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddHarmonyRateLimiting();
}

// -----------------------------------------------------------------------
// Infrastructure (Postgres, Scylla, RabbitMQ, SignalR backplane, repos,
// services, consumers)
// -----------------------------------------------------------------------
builder.Services.AddInfrastructureServices(builder.Configuration);

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
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// =======================================================================
var app = builder.Build();

// =======================================================================

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Trigger RabbitMQ connection and topology declaration asynchronously without blocking
var rabbitConnection = app.Services.GetRequiredService<RabbitMQConnection>();
await using (var startupChannel = await rabbitConnection.CreateChannelAsync())
{
    // Simply opening and disposing a channel triggers the lazy asynchronous connection
    // and topology declaration cleanly without blocking the startup thread pool.
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseCors("HarmonyClient");
if (!app.Environment.IsEnvironment("Test"))
{
    app.UseRateLimiter();
}
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
