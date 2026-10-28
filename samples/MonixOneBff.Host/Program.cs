using MonixOne.OidcProxy.ModuleInitializers;
using MonixOne.OidcProxy.OpenIdConnect;
using StackExchange.Redis;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration.GetRequiredSection("OidcProxy")
    .Get<OidcProxyConfig>(binder => binder.ErrorOnUnknownConfiguration = true)
    ?? throw new InvalidOperationException("OidcProxy configuration is required.");
var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");
var project = builder.Configuration["ProjectName"]
    ?? throw new InvalidOperationException("ProjectName is required.");
var keyNamespace = builder.Configuration["RedisNamespace"]
    ?? throw new InvalidOperationException("RedisNamespace is required.");

// The host owns this single multiplexer and disposes it after the application stops.
await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnection);
builder.Services.AddOidcProxy(config, options =>
    options.ConfigureRedisBackBone(redis, project, builder.Environment.EnvironmentName, keyNamespace));

await using var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseOidcProxy();
app.MapGet("/demo-api/status", (HttpContext context, TimeProvider clock) => Results.Ok(new
{
    Service = "MonixOneBff.Host",
    Subject = context.User.FindFirstValue("sub"),
    Environment = app.Environment.EnvironmentName,
    Utc = clock.GetUtcNow()
})).AsOidcProxyApi();
await app.RunAsync();
