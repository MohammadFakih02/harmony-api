using System.Text;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Services;
using Harmony.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Snowflake ID generator
var workerId = builder.Configuration.GetValue<long>("Snowflake:WorkerId", 0);
var datacenterId = builder.Configuration.GetValue<long>("Snowflake:DatacenterId", 0);
builder.Services.AddSingleton<ISnowflakeIdGenerator>(_ => new SnowflakeIdGenerator(
    workerId,
    datacenterId
));

// Database
builder.Services.AddDbContext<HarmonyDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
);

// Data protection (required by Identity for token providers)
builder.Services.AddDataProtection();

// Identity
builder
    .Services.AddIdentityCore<User>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<HarmonyDbContext>()
    .AddDefaultTokenProviders();

// JWT authentication
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
            ClockSkew = TimeSpan.Zero, // no grace period — 15 min means 15 min
        };

        // Let SignalR hubs pick up the JWT from the query string
        // (browsers can't set Authorization headers on WebSocket connections)
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

// Harmony services
builder.Services.AddScoped<IJwtService, JwtService>();

// Controllers + OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// -----------------------------------------------------------------------

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

app.UseAuthentication(); // must be before UseAuthorization
app.UseAuthorization();

app.MapControllers();

app.Run();
