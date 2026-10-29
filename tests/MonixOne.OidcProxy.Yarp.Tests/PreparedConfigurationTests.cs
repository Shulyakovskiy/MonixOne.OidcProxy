using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Yarp.ReverseProxy.Forwarder;
using SpaHostProgram = MonixOne.OidcProxy.Yarp.SampleHost.Program;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class PreparedConfigurationTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Profile_BrAdminIdentityConfiguration_RegistersTheClientAndPassesCalendarCookies()
    {
        // Arrange: use the documented JSON without replacing its route or client collection.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure, "br-admin-identity-bff");
        identity.Server.Given(Request.Create().WithPath("/calendar-api/connections/google/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Set-Cookie", "calendar_google_state=prepared-state; Path=/admin/calendar-api/connections/google/callback; Max-Age=600; SameSite=Lax; Secure; HttpOnly"));
        identity.Server.Given(Request.Create().WithPath("/calendar-api/connections/google/callback").UsingGet()
            .WithHeader("Cookie", "calendar_google_state=prepared-state"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("calendar-connected"));
        await using var host = new PreparedHost(infrastructure, identity);
        using var browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://admin-dev.intra-beetroute.ru"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider, "/admin");
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/calendar-api/connections/google/start");
        startRequest.Headers.Add("X-CSRF", "1");
        using var start = await browser.SendAsync(startRequest, ct);
        using var callback = await browser.GetAsync("/admin/calendar-api/connections/google/callback", ct);
        using var resource = await browser.ApiAsync("/admin/api/status", ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await callback.Content.ReadAsStringAsync(ct)).ShouldBe("calendar-connected");
        resource.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.BackendRequests.Single().Headers!["Authorization"].Single().ShouldBe("Bearer " + identity.LastAccessToken);
    }

    /// <summary>
    /// Loads the documented client/routes before the real Program starts.
    /// Only external addresses, test secrets and certificate trust are replaced.
    /// </summary>
    private sealed class PreparedHost(InfrastructureFixture infrastructure, OidcWireMock identity) : WebApplicationFactory<SpaHostProgram>
    {
        private static readonly string ProfileRoot = Path.Combine(AppContext.BaseDirectory, "Profiles", "BrAdminIdentity");
        private readonly string _scope = Guid.NewGuid().ToString("N");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing").UseContentRoot(ProfileRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:admin-backend:Destinations:primary:Address"] = identity.Origin.AbsoluteUri,
                ["ReverseProxy:Clusters:beetroute-apis:Destinations:primary:Address"] = identity.Origin.AbsoluteUri
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ConnectionMultiplexer>();
                services.AddSingleton(infrastructure.Connection);
                services.PostConfigure<ProxyOptions>(options => options.KeyNamespace = _scope);
                services.Configure<OpenIdConnectOptions>(OidcProxyDefaults.OpenIdConnectScheme("adminidentity"), options =>
                {
                    options.Authority = identity.Origin.AbsoluteUri.TrimEnd('/');
                    options.ClientSecret = OidcWireMock.ClientSecret;
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == infrastructure.Certificate.Thumbprint
                    };
                });
                services.RemoveAll<IForwarderHttpClientFactory>();
                services.AddSingleton<IForwarderHttpClientFactory>(new PinnedForwarder(infrastructure));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostDefaults.ContentRootKey] = ProfileRoot
            }).AddJsonFile(Path.Combine(ProfileRoot, "appsettings.json")));
            return base.CreateHost(builder);
        }
    }

    private sealed class PinnedForwarder(InfrastructureFixture infrastructure) : ForwarderHttpClientFactory
    {
        protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
        {
            base.ConfigureHandler(context, handler);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate?.GetCertHashString() == infrastructure.Certificate.Thumbprint;
        }
    }
}
