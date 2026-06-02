using System.Text;
using Harmony.API.Extensions;
using Harmony.API.Handlers;
using Harmony.Domain.Domain.Entities;
using Harmony.Application.Services;
using Harmony.Infrastructure.Extensions;
using Harmony.Infrastructure.RabbitMQ;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Snowflake ID generator
var workerId = builder.Configuration.GetValue<long>("Snowflake:WorkerId", 0);
var datacenterId = builder.Configuration.GetValue<long>("Snowflake:DatacenterId", 0);
builder.Services.AddSingleton<ISnowflakeIdGenerator>(_ => new SnowflakeIdGenerator(
    workerId,
    datacenterId
));

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "HarmonyClient",
        policy =>
        {
            policy
                .WithOrigins("http://localhost:4200")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials(); // required for httpOnly cookies
        }
    );
});

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
    .AddEntityFrameworkStores<Harmony.Infrastructure.Postgres.HarmonyDbContext>()
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

// Rate limiting
builder.Services.AddHarmonyRateLimiting();

// Infrastructure, Repositories, Consumers and Core Services registration
builder.Services.AddInfrastructureServices(builder.Configuration);

// Centralized Global Exception Handling
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Controllers + OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// -----------------------------------------------------------------------

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Force RabbitMQ connection and topology declaration on startup
app.Services.GetRequiredService<RabbitMQConnection>();

app.UseHttpsRedirection();
app.UseCors("HarmonyClient");

app.UseRateLimiter(); // before auth so login rate limit hits before Identity runs

app.UseExceptionHandler(); // Register Global Exception Handler middleware

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
