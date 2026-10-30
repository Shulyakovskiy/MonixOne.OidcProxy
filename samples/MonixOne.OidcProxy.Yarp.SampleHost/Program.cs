namespace MonixOne.OidcProxy.Yarp.SampleHost;

/// <summary>
/// Hosts the React SPA and its YARP-backed OIDC BFF.
/// </summary>
public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOidcProxy(builder.Configuration);
        var app = builder.Build();
        app.UseOidcProxy();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("{**path}", "index.html").AllowAnonymous();
        await app.RunAsync();
    }
}
