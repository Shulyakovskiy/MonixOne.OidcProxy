using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using SpaHostProgram = MonixOne.OidcProxy.Yarp.SampleHost.Program;

namespace MonixOne.OidcProxy.Yarp.Tests;

/// <summary>
/// Runs the actual SPA host through WebApplicationFactory. External HTTPS calls
/// use real sockets and trust only the fixture certificate, never arbitrary TLS.
/// </summary>
public sealed class BffApplicationFactory(
    InfrastructureFixture infrastructure,
    Uri authority,
    string clientId,
    string clientSecret,
    Uri backend,
    string? keyNamespace = null,
    ConnectionMultiplexer? connection = null
) : WebApplicationFactory<SpaHostProgram>
{
    public static readonly Uri Origin = new("https://spa.example");
    public Uri PublicOrigin { get; set; } = Origin;
    public ManualClock Clock { get; } = new();
    public string KeyNamespace { get; } = keyNamespace ?? Guid.NewGuid().ToString("N");
    public string? EncryptionKey { get; set; } = infrastructure.EncryptionKey;
    public Action<OidcProxyClientOptions>? ConfigureClientOptions { get; set; }
    public Action<OpenIdConnectOptions>? ConfigureOidc { get; set; }
    public string PathBase { get; set; } = string.Empty;
    public string ProxyPath { get; set; } = "/api/{**all}";
    public string? WebRoot { get; set; }
    public Action<List<ClusterConfig>>? ConfigureClusters { get; set; }
    public Action<List<RouteConfig>>? ConfigureRoutes { get; set; }
    public Dictionary<string, string?> ConfigurationValues { get; } = [];
    internal IDataProtector SessionProtector =>
        Services
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(
                "MonixOne.OidcProxy.Yarp.ServerSession",
                "test-spa:testing:" + KeyNamespace + ":spa:"
            );
    private readonly List<(
        string Name,
        Uri Origin,
        Uri Authority,
        string ClientId,
        string Secret,
        Uri Backend,
        string PathBase
    )> _clients = [];

    internal void AddClient(string name, Uri origin, OidcWireMock identity, string pathBase = "") =>
        AddClient(
            name,
            origin,
            identity.Origin,
            identity.RegisteredClientId,
            OidcWireMock.ClientSecret,
            identity.Origin,
            pathBase
        );

    internal void AddClient(
        string name,
        Uri origin,
        Uri authority,
        string clientId,
        string secret,
        Uri backend,
        string pathBase = ""
    ) => _clients.Add((name, origin, authority, clientId, secret, backend, pathBase));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        if (WebRoot is not null)
            builder.UseWebRoot(WebRoot);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ConnectionMultiplexer>();
            services.AddSingleton(connection ?? infrastructure.Connection);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<ProxyOptions>(options =>
            {
                options.ApplicationName = "test-spa";
                options.EnvironmentName = "testing";
                options.KeyNamespace = KeyNamespace;
            });
            services.PostConfigure<OidcProxyClientOptions>(
                "spa",
                options =>
                {
                    options.PublicOrigin = PublicOrigin;
                    options.PathBase = PathBase;
                    ConfigureClientOptions?.Invoke(options);
                }
            );
            services.Configure<OpenIdConnectOptions>(
                OidcProxyDefaults.OpenIdConnectScheme("spa"),
                options =>
                {
                    options.Authority = authority.AbsoluteUri.TrimEnd('/');
                    options.ClientId = clientId;
                    options.ClientSecret = clientSecret;
                    options.TimeProvider = Clock;
                    options.Scope.Clear();
                    foreach (var scope in new[] { "openid", "offline_access", "api.read" })
                        options.Scope.Add(scope);
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                            certificate?.Thumbprint == infrastructure.Certificate.Thumbprint,
                    };
                    ConfigureOidc?.Invoke(options);
                }
            );
            services.Configure<CookieAuthenticationOptions>(
                OidcProxyDefaults.CookieScheme("spa"),
                options => options.TimeProvider = Clock
            );
            services.RemoveAll<IProxyConfigProvider>();
            var routes = new List<RouteConfig>
            {
                new()
                {
                    RouteId = "api",
                    ClusterId = "api",
                    Match = new() { Path = ProxyPath, Hosts = [PublicOrigin.Authority] },
                },
            };
            var clusters = new List<ClusterConfig>
            {
                new()
                {
                    ClusterId = "api",
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["primary"] = new() { Address = backend.AbsoluteUri },
                    },
                },
            };
            foreach (var client in _clients)
            {
                services.Configure<OpenIdConnectOptions>(
                    OidcProxyDefaults.OpenIdConnectScheme(client.Name),
                    options =>
                    {
                        options.TimeProvider = Clock;
                        options.BackchannelHttpHandler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                                certificate?.Thumbprint == infrastructure.Certificate.Thumbprint,
                        };
                    }
                );
                services.Configure<CookieAuthenticationOptions>(
                    OidcProxyDefaults.CookieScheme(client.Name),
                    options => options.TimeProvider = Clock
                );
                routes.Add(
                    new RouteConfig
                    {
                        RouteId = client.Name,
                        ClusterId = client.Name,
                        Match = new() { Path = "/api/{**all}", Hosts = [client.Origin.Authority] },
                    }
                );
                clusters.Add(
                    new ClusterConfig
                    {
                        ClusterId = client.Name,
                        Destinations = new Dictionary<string, DestinationConfig>
                        {
                            ["primary"] = new() { Address = client.Backend.AbsoluteUri },
                        },
                    }
                );
            }
            ConfigureRoutes?.Invoke(routes);
            ConfigureClusters?.Invoke(clusters);
            services.AddSingleton<IProxyConfigProvider>(
                new InMemoryConfigProvider(routes, clusters)
            );
            services.RemoveAll<IForwarderHttpClientFactory>();
            services.AddSingleton<IForwarderHttpClientFactory>(
                new CertificateForwarderFactory(infrastructure.Certificate)
            );
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration =>
        {
            var values = new Dictionary<string, string?>(ConfigurationValues)
            {
                ["Bff:EncryptionKey"] = EncryptionKey,
            };
            foreach (var client in _clients)
            {
                var prefix = "Bff:Clients:" + client.Name + ":";
                values[prefix + "PublicOrigin"] = client.Origin.AbsoluteUri;
                values[prefix + "Routes:" + client.Name + ":AllowAnonymous"] = "false";
                values[prefix + "PathBase"] = client.PathBase;
                values[prefix + "OpenIdConnect:Authority"] = client.Authority.AbsoluteUri.TrimEnd(
                    '/'
                );
                values[prefix + "OpenIdConnect:ClientId"] = client.ClientId;
                values[prefix + "OpenIdConnect:ClientSecret"] = client.Secret;
                foreach (
                    var (scope, index) in new[] { "openid", "offline_access", "api.read" }.Select(
                        (value, index) => (value, index)
                    )
                )
                    values[prefix + "OpenIdConnect:Scope:" + index] = scope;
            }
            configuration.AddInMemoryCollection(values);
        });
        return base.CreateHost(builder);
    }

    public HttpClient CreateBrowser(bool handleCookies = true) =>
        CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = PublicOrigin,
                AllowAutoRedirect = false,
                HandleCookies = handleCookies,
            }
        );

    private sealed class CertificateForwarderFactory(X509Certificate2 certificate)
        : ForwarderHttpClientFactory
    {
        protected override void ConfigureHandler(
            ForwarderHttpClientContext context,
            SocketsHttpHandler handler
        )
        {
            base.ConfigureHandler(context, handler);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, server, _, _) =>
                server?.GetCertHashString() == certificate.Thumbprint;
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
    internal static async Task<HttpResponseMessage> ApiAsync(
        this HttpClient browser,
        string path = "/api/status",
        CancellationToken cancellationToken = default,
        string? origin = null,
        bool csrf = true,
        string? bearer = null
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (csrf)
            request.Headers.Add("X-CSRF", "1");
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (bearer is not null)
            request.Headers.Add("Authorization", "Bearer " + bearer);
        return await browser.SendAsync(request, cancellationToken);
    }

    internal static async Task<HttpResponseMessage> LoginAsync(
        this HttpClient browser,
        HttpClient identity,
        string pathBase = "",
        string? returnUrl = null,
        Uri? origin = null,
        bool? rememberMe = null
    )
    {
        var path = pathBase + OidcProxyRoutes.Login;
        if (rememberMe.HasValue)
            path = QueryHelpers.AddQueryString(path, "rememberMe", rememberMe.Value.ToString());
        if (returnUrl is not null)
            path = QueryHelpers.AddQueryString(path, "returnUrl", returnUrl);
        var start = await browser.GetAsync(
            origin is null ? path : new Uri(origin, path).AbsoluteUri
        );
        if (start.StatusCode != HttpStatusCode.Redirect)
            return start;
        using var started = start;
        using var authorize = await identity.GetAsync(start.Headers.Location);
        if (authorize.StatusCode != HttpStatusCode.Redirect)
            return new HttpResponseMessage(authorize.StatusCode)
            {
                Content = new StringContent(await authorize.Content.ReadAsStringAsync()),
            };
        return await browser.GetAsync(authorize.Headers.Location!);
    }

    internal static async Task<HttpResponseMessage> LogoutAsync(
        this HttpClient browser,
        bool nativeForm = false,
        CancellationToken cancellationToken = default,
        string pathBase = "",
        string? returnUrl = null,
        Uri? origin = null
    )
    {
        var path = pathBase + OidcProxyRoutes.Logout;
        if (returnUrl is not null)
            path = QueryHelpers.AddQueryString(path, "returnUrl", returnUrl);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            origin is null ? path : new Uri(origin, path).AbsoluteUri
        );
        if (nativeForm)
        {
            using var response = await browser.ApiAsync(
                origin is null
                    ? pathBase + OidcProxyRoutes.Antiforgery
                    : new Uri(origin, pathBase + OidcProxyRoutes.Antiforgery).AbsoluteUri,
                cancellationToken
            );
            var tokens = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            request.Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    [tokens.GetProperty("formFieldName").GetString()!] = tokens
                        .GetProperty("requestToken")
                        .GetString()!,
                }
            );
        }
        else
            request.Headers.Add("X-CSRF", "1");
        return await browser.SendAsync(request, cancellationToken);
    }
}
