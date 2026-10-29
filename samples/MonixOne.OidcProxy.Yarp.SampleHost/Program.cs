using StackExchange.Redis;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography.X509Certificates;

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
        builder.Services.AddOidcProxy(builder.Configuration.GetRequiredSection("Bff"));
        using var keyEncryptionCertificate = builder.Configuration["DataProtection:CertificatePath"] is { Length: > 0 } path
            ? X509CertificateLoader.LoadPkcs12FromFile(path, builder.Configuration["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet)
            : null;
        if (keyEncryptionCertificate is not null)
            builder.Services.AddDataProtection().ProtectKeysWithCertificate(keyEncryptionCertificate);

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
