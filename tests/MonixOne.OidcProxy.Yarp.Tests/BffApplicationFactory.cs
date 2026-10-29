using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using SpaHostProgram = MonixOne.OidcProxy.Yarp.SampleHost.Program;

namespace MonixOne.OidcProxy.Yarp.Tests;

/// <summary>
/// Runs the actual SPA host through WebApplicationFactory. External HTTPS calls
/// use real sockets and trust only the fixture certificate, never arbitrary TLS.
/// </summary>
public sealed class BffApplicationFactory(InfrastructureFixture infrastructure, Uri authority,
    string clientId, string clientSecret, Uri backend, string? keyNamespace = null,
    ConnectionMultiplexer? connection = null) : WebApplicationFactory<SpaHostProgram>
{
    public static readonly Uri Origin = new("https://spa.example");
    public ManualClock Clock { get; } = new();
    public string KeyNamespace { get; } = keyNamespace ?? Guid.NewGuid().ToString("N");
    public Action<OpenIdConnectOptions>? ConfigureOidc { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ConnectionMultiplexer>();
            services.AddSingleton(connection ?? infrastructure.Connection);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<ProxyOptions>(options =>
            {
                options.PublicOrigin = Origin;
                options.ApplicationName = "test-spa";
                options.EnvironmentName = "testing";
                options.KeyNamespace = KeyNamespace;
            });
            services.Configure<OpenIdConnectOptions>(OidcProxyDefaults.OpenIdConnectScheme, options =>
            {
                options.Authority = authority.AbsoluteUri.TrimEnd('/');
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.Scope.Clear();
                foreach (var scope in new[] { "openid", "offline_access", "api.read" }) options.Scope.Add(scope);
                options.BackchannelHttpHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                        certificate?.Thumbprint == infrastructure.Certificate.Thumbprint
                };
                ConfigureOidc?.Invoke(options);
            });
            services.Configure<CookieAuthenticationOptions>(OidcProxyDefaults.CookieScheme, options => options.TimeProvider = Clock);
            services.RemoveAll<IProxyConfigProvider>();
            services.AddSingleton<IProxyConfigProvider>(new InMemoryConfigProvider(
                [new RouteConfig { RouteId = "api", ClusterId = "api", Match = new() { Path = "/api/{**all}" } }],
                [new ClusterConfig { ClusterId = "api", Destinations = new Dictionary<string, DestinationConfig>
                    { ["primary"] = new() { Address = backend.AbsoluteUri } } }]));
            services.RemoveAll<IForwarderHttpClientFactory>();
            services.AddSingleton<IForwarderHttpClientFactory>(new CertificateForwarderFactory(infrastructure.Certificate));
        });
    }

    public HttpClient CreateBrowser(bool handleCookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = Origin,
        AllowAutoRedirect = false,
        HandleCookies = handleCookies
    });

    private sealed class CertificateForwarderFactory(X509Certificate2 certificate) : ForwarderHttpClientFactory
    {
        protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
        {
            base.ConfigureHandler(context, handler);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, server, _, _) => server?.GetCertHashString() == certificate.Thumbprint;
        }
    }
}

/// <summary>
/// Advances session policy time without sleeps; TLS and JWT validation retain
/// the framework clock because those protocols cross real HTTP processes.
/// </summary>
public sealed class ManualClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan interval) => Interlocked.Add(ref _ticks, interval.Ticks);
}

/// <summary>
/// Performs browser cookie requests and the real redirect/code callback dance.
/// Tests own their assertions; this helper never asserts a successful login.
/// </summary>
internal static class BrowserRequests
{
    internal static async Task<HttpResponseMessage> ApiAsync(this HttpClient browser, string path = "/api/status",
        CancellationToken cancellationToken = default, string? origin = null, bool csrf = true, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (csrf) request.Headers.Add("X-CSRF", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (bearer is not null) request.Headers.Add("Authorization", "Bearer " + bearer);
        return await browser.SendAsync(request, cancellationToken);
    }

    internal static async Task<HttpResponseMessage> LoginAsync(this HttpClient browser, HttpClient identity)
    {
        var start = await browser.GetAsync("/.auth/login");
        if (start.StatusCode != HttpStatusCode.Redirect) return start;
        using var started = start;
        using var authorize = await identity.GetAsync(start.Headers.Location);
        if (authorize.StatusCode != HttpStatusCode.Redirect)
            return new HttpResponseMessage(authorize.StatusCode) { Content = new StringContent(await authorize.Content.ReadAsStringAsync()) };
        return await browser.GetAsync(authorize.Headers.Location!.PathAndQuery);
    }

    internal static async Task<HttpResponseMessage> LogoutAsync(this HttpClient browser, bool nativeForm = false,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/.auth/end-session");
        if (nativeForm)
        {
            using var response = await browser.ApiAsync("/.auth/antiforgery", cancellationToken);
            var tokens = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [tokens.GetProperty("formFieldName").GetString()!] = tokens.GetProperty("requestToken").GetString()!
            });
        }
        else request.Headers.Add("X-CSRF", "1");
        return await browser.SendAsync(request, cancellationToken);
    }
}
