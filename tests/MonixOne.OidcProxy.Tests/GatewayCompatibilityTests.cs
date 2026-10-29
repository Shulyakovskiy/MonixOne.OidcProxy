using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MonixOne.OidcProxy.ModuleInitializers;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MonixOne.OidcProxy.Tests;

public sealed class GatewayCompatibilityTests : IAsyncLifetime
{
    private readonly FakeIdentityProvider _idp = new();
    private readonly RSA _rsa = RSA.Create(2048);
    private BffHost _host = null!;
    private Browser _browser = null!;
    private string _machineToken = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var clock = new TestClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        var key = new RsaSecurityKey(_rsa) { KeyId = "gateway-test-key" };
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = "https://machine.example", ValidAudience = "beetroute-api",
                IssuerSigningKey = key, ValidTypes = ["at+jwt"]
            };
        });
        builder.Services.AddAuthorization(authorization =>
        {
            authorization.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().RequireClaim("host", "machine").Build();
            authorization.FallbackPolicy = authorization.DefaultPolicy;
            authorization.AddPolicy("machine", policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireClaim("scope", "profile-service.full_access"));
            authorization.AddPolicy("manager", policy => policy.RequireRole("manager"));
        });
        var routes = new Dictionary<string, RouteConfig>
        {
            ["browser"] = new()
            {
                Match = new() { Path = "/browser/{**all}" }, ClusterId = "api",
                Metadata = new Dictionary<string, string> { [ProxyOptions.BffRouteMetadataKey] = "true" },
                Transforms = [new Dictionary<string, string> { ["PathRemovePrefix"] = "/browser" }]
            },
            ["machine"] = new() { Match = new() { Path = "/machine/{**all}" }, ClusterId = "api", AuthorizationPolicy = "machine" },
            ["public"] = new() { Match = new() { Path = "/public/{**all}" }, ClusterId = "api", AuthorizationPolicy = "anonymous" }
        };
        builder.Services.AddReverseProxy().LoadFromMemory(routes.ToRouteConfig(),
            [new ClusterConfig { ClusterId = "api", Destinations = new Dictionary<string, DestinationConfig>
                { ["one"] = new() { Address = "https://api.example/" } } }]);
        var options = new ProxyOptions { ProtectOnlyMarkedRoutes = true };
        options.SetCustomHostName(BffHost.Origin);
        builder.Services.AddOidcProxy(options, oidc =>
        {
            oidc.Authority = FakeIdentityProvider.Authority;
            oidc.ClientId = FakeIdentityProvider.ClientId;
            oidc.ClientSecret = FakeIdentityProvider.ClientSecret;
            oidc.Configuration = _idp.Configuration();
            oidc.Backchannel = new HttpClient(_idp, disposeHandler: false);
        });
        builder.Services.AddSingleton<IForwarderHttpClientFactory, GatewayEchoClientFactory>();
        var app = builder.Build();
        app.UseOidcProxy();
        app.MapGet("/host-api", () => "host").RequireAuthorization();
        app.MapGet("/local-bff", () => "bff").AsOidcProxyApi();
        app.MapGet("/role-bff", () => "bff").AsOidcProxyApi().RequireAuthorization("manager");
        await app.StartAsync();
        _host = new BffHost(app, clock);
        _browser = new Browser(_host);
        _machineToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://machine.example", Audience = "beetroute-api", TokenType = "at+jwt",
            Subject = new ClaimsIdentity([new Claim("sub", "service"), new Claim("host", "machine"),
                new Claim("scope", "profile-service.full_access")]),
            Expires = DateTime.UtcNow.AddMinutes(5), SigningCredentials = new(key, SecurityAlgorithms.RsaSha256)
        });
    }

    [Fact]
    public async Task HostDefaultSchemeAndAuthorizationPolicyArePreserved()
    {
        var options = _host.App.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, options.DefaultScheme);
        var bearer = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _machineToken };
        Assert.Equal(HttpStatusCode.OK, (await _browser.SendAsync(HttpMethod.Get, "/host-api", csrf: false, headers: bearer)).StatusCode);
        await _browser.LoginAsync(_idp);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _browser.SendAsync(HttpMethod.Get, "/host-api", csrf: false)).StatusCode);
    }

    [Theory]
    [InlineData("/browser/data")]
    [InlineData("/.auth/me")]
    [InlineData("/local-bff")]
    public async Task BffRequiresItsCookieAndDoesNotInheritTheHostsDefaultPolicy(string path)
    {
        var bearer = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _machineToken };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _browser.SendAsync(HttpMethod.Get, path, headers: bearer)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await _browser.LoginAsync(_idp)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _browser.SendAsync(HttpMethod.Get, path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _browser.SendAsync(HttpMethod.Get, path, csrf: false)).StatusCode);
    }

    [Fact]
    public async Task MarkedRouteReplacesBrowserCredentialsAndPreservesYarpPathTransforms()
    {
        await _browser.LoginAsync(_idp);
        var response = await _browser.SendAsync(HttpMethod.Get, "/browser/data", headers:
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + _machineToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = (await response.Content.ReadFromJsonAsync<GatewayEcho>())!;
        Assert.Equal(_idp.LastAccessToken, echo.Token);
        Assert.Null(echo.Cookie);
        Assert.Equal("/data", echo.Path);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task MachineRouteKeepsBearerAndOtherCookiesButNeverReceivesBffCredentials()
    {
        await _browser.LoginAsync(_idp);
        var cookies = _browser.Cookies + "; __Host-oidcproxy.session.csrf=csrf-cookie; other=preserved";
        var response = await _browser.SendAsync(HttpMethod.Get, "/machine/data", csrf: false, cookies: cookies,
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + _machineToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = (await response.Content.ReadFromJsonAsync<GatewayEcho>())!;
        Assert.Equal(_machineToken, echo.Token);
        Assert.Equal("other=preserved", echo.Cookie);
        Assert.True(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-oidcproxy.session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BffAuthorizationUsesRefreshedRolesInsteadOfTheCachedHostOrCookiePrincipal()
    {
        await _browser.LoginAsync(_idp);
        Assert.Equal(HttpStatusCode.OK, (await _browser.SendAsync(HttpMethod.Get, "/role-bff")).StatusCode);
        _idp.Role = "employee";
        _host.Clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(HttpStatusCode.Forbidden, (await _browser.SendAsync(HttpMethod.Get, "/role-bff")).StatusCode);
        _idp.Role = "manager";
        _host.Clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(HttpStatusCode.OK, (await _browser.SendAsync(HttpMethod.Get, "/role-bff")).StatusCode);
    }

    [Fact]
    public async Task UnmarkedAnonymousRouteDoesNotAcquireBffCsrfOrAuthorizationRequirements()
    {
        Assert.Equal(HttpStatusCode.OK, (await _browser.SendAsync(HttpMethod.Get, "/public/data", csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _browser.SendAsync(HttpMethod.Get, "/machine/data", csrf: false)).StatusCode);
    }

    [Fact]
    public async Task ReloadedRouteAcquiresBffProtectionAndTokenTransformsTogether()
    {
        Assert.Equal(HttpStatusCode.OK, (await _browser.SendAsync(HttpMethod.Get, "/public/data", csrf: false)).StatusCode);
        var provider = Assert.IsType<InMemoryConfigProvider>(_host.App.Services.GetRequiredService<IProxyConfigProvider>());
        var previous = provider.GetConfig();
        provider.Update(previous.Routes.Select(route => route.RouteId == "public"
            ? route with { Metadata = new Dictionary<string, string> { [ProxyOptions.BffRouteMetadataKey] = "true" } }
            : route).ToArray(), previous.Clusters);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        HttpStatusCode status;
        do
        {
            using var response = await _browser.SendAsync(HttpMethod.Get, "/public/data", ct: timeout.Token);
            status = response.StatusCode;
            if (status != HttpStatusCode.OK) break;
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        } while (!timeout.IsCancellationRequested);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        await _browser.LoginAsync(_idp);
        Assert.Equal(HttpStatusCode.Forbidden, (await _browser.SendAsync(HttpMethod.Get, "/public/data", csrf: false)).StatusCode);
        var authenticated = await _browser.SendAsync(HttpMethod.Get, "/public/data");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(_idp.LastAccessToken, (await authenticated.Content.ReadFromJsonAsync<GatewayEcho>())!.Token);
        Assert.False(authenticated.Headers.Contains("Set-Cookie"));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        _idp.Dispose();
        _rsa.Dispose();
    }

    private sealed record GatewayEcho(string? Token, string? Cookie, string Path);

    private sealed class GatewayEchoClientFactory : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new GatewayEchoHandler());
    }

    private sealed class GatewayEchoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GatewayEcho(request.Headers.Authorization?.Parameter,
                    request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
                    request.RequestUri!.AbsolutePath))
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "other=downstream; Path=/");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "__Host-oidcproxy.session=forged; Path=/; Secure");
            return Task.FromResult(response);
        }
    }
}
