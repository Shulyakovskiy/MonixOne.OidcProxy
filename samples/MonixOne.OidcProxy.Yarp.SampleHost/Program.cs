using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.SampleHost;

/// <summary>
/// Hosts the React SPA and its YARP-backed OIDC BFF.
/// </summary>
public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // The host owns and disposes one multiplexer through its DI container.
        builder.Services.AddSingleton(sp => ConnectionMultiplexer.Connect(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.")));
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetRequiredSection("ReverseProxy"));
        builder.Services.AddOidcProxy(options =>
        {
            builder.Configuration.GetRequiredSection("Bff").Bind(options);
            options.EnvironmentName = builder.Environment.EnvironmentName;
        });
        foreach (var section in builder.Configuration.GetRequiredSection("Bff:Clients").GetChildren())
            builder.Services.AddOidcProxyClient(section.Key,
                client => section.Bind(client),
                oidc => section.GetRequiredSection("OpenIdConnect").Bind(oidc));

        await using var app = builder.Build();
        app.Services.GetRequiredService<ConnectionMultiplexer>();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.UseHttpsRedirection();
        app.UseOidcProxy();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("{**path}", "index.html").AllowAnonymous();
        await app.RunAsync();
    }
}
