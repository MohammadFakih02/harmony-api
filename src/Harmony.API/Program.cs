using Harmony.Core.Services;

var builder = WebApplication.CreateBuilder(args);


var workerId = builder.Configuration.GetValue<long>("Snowflake:WorkerId", 0);
var datacenterId = builder.Configuration.GetValue<long>("Snowflake:DatacenterId", 0);

builder.Services.AddSingleton<ISnowflakeIdGenerator>(
    _ => new SnowflakeIdGenerator(workerId, datacenterId));

builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();



