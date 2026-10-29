using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using MonixOne.OidcProxy.ModuleInitializers;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Testcontainers.PostgreSql;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MonixOne.OidcProxy.Tests;

public sealed class OpenIddictFixture : IAsyncLifetime
{
    internal const string Authority = "https://identity.example/";
    internal const string Scope = "profile-service.full_access";
    internal const string Subject = "d01e47cf-bd88-43a4-8714-42e6f907c082";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    internal WebApplication App { get; private set; } = null!;
    internal HttpClient Client { get; private set; } = null!;
    internal int RefreshRequests;
    internal int RevocationRequests;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<CompatibilityDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()).UseOpenIddict());
        builder.Services.AddAuthentication("identity-session").AddCookie("identity-session", cookie =>
        {
            cookie.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<CompatibilityDbContext>())
            .AddServer(server =>
            {
                server.SetIssuer(new Uri(Authority)).SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token").SetRevocationEndpointUris("/connect/revocation")
                    .SetEndSessionEndpointUris("/connect/logout").SetJsonWebKeySetEndpointUris("/.well-known/jwks")
                    .AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange()
                    .RegisterScopes(Scope).SetAccessTokenLifetime(TimeSpan.FromMinutes(2))
                    .SetRefreshTokenLifetime(TimeSpan.FromMinutes(20)).DisableSlidingRefreshTokenExpiration()
                    .SetRefreshTokenReuseLeeway(TimeSpan.Zero).DisableAccessTokenEncryption()
                    .AddEphemeralSigningKey().AddEphemeralEncryptionKey();
                server.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableEndSessionEndpointPassthrough();
                server.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(handler => handler.UseInlineHandler(context =>
                {
                    context.Response.SetParameter("ok", string.IsNullOrEmpty(context.Response.Error));
                    return ValueTask.CompletedTask;
                }));
            });
        App = builder.Build();
        App.Use(async (context, next) =>
        {
            // BR.Identity selects its tenant before OpenIddict from client_id in query/form.
            // Test the boundary without referencing or modifying the service repository.
            if (context.Request.Path.StartsWithSegments("/connect"))
            {
                var clientId = context.Request.Query["client_id"].ToString();
                if (context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    clientId = form["client_id"].ToString();
                    if (form["grant_type"] == "refresh_token") Interlocked.Increment(ref RefreshRequests);
                }
                if (context.Request.Path == "/connect/revocation") Interlocked.Increment(ref RevocationRequests);
                if (string.IsNullOrEmpty(clientId))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "client_id is required for tenant selection" });
                    return;
                }
            }
            await next(context);
        });
        App.UseAuthentication();
        App.MapPost("/test/session", () => Results.SignIn(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Subject)], "identity-session")), authenticationScheme: "identity-session"));
        App.MapGet("/connect/authorize", async (HttpContext context) =>
        {
            var session = await context.AuthenticateAsync("identity-session");
            if (!session.Succeeded) return Results.Challenge(authenticationSchemes: ["identity-session"]);
            var request = context.GetOpenIddictServerRequest()!;
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", Subject),
                new Claim("tenant_id", "mbr").SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken),
                new Claim("role", "manager").SetDestinations(OpenIddictConstants.Destinations.AccessToken)
            ], "identity-session"));
            principal.SetScopes(request.GetScopes());
            principal.SetResources("beetroute-api");
            return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        });
        App.MapGet("/connect/logout", async (HttpContext context) =>
        {
            var hint = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (hint.Principal?.GetClaim("tenant_id") != "mbr") return Results.BadRequest();
            return Results.SignOut(authenticationSchemes: ["identity-session", OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        });
        await using (var scope = App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CompatibilityDbContext>().Database.EnsureCreatedAsync();
        await App.StartAsync();
        Client = App.GetTestClient();
        Client.BaseAddress = new Uri(Authority);
    }

    internal async Task<(BffHost Host, string ClientId, string Secret)> CreateBffAsync(bool publicClient = false, string requestedScope = Scope)
    {
        var clientId = "bff-" + Guid.NewGuid().ToString("N");
        const string secret = "test-only-confidential-secret";
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId, ClientSecret = publicClient ? null : secret,
                ClientType = publicClient ? OpenIddictConstants.ClientTypes.Public : OpenIddictConstants.ClientTypes.Confidential,
                ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                RedirectUris = { new Uri(BffHost.Origin, "/.auth/login/callback") },
                PostLogoutRedirectUris = { new Uri(BffHost.Origin, "/.auth/signout-callback") },
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization, OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.Revocation, OpenIddictConstants.Permissions.Endpoints.EndSession,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code, OpenIddictConstants.Permissions.Prefixes.Scope + Scope
                }
            };
            await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(descriptor);
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var clock = new TestClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        var options = new ProxyOptions();
        options.SetCustomHostName(BffHost.Origin);
        options.ConfigureYarp([new RouteConfig { RouteId = "api", ClusterId = "api", Match = new() { Path = "/api/{**all}" } }],
            [new ClusterConfig { ClusterId = "api", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "https://api.example/" } } }]);
        builder.Services.AddOidcProxy(options, oidc =>
        {
            oidc.Authority = Authority;
            oidc.ClientId = clientId;
            oidc.ClientSecret = secret;
            oidc.Scope.Add(requestedScope);
            oidc.BackchannelHttpHandler = App.GetTestServer().CreateHandler();
        });
        builder.Services.AddSingleton<IForwarderHttpClientFactory, OpenIddictEchoFactory>();
        var app = builder.Build();
        app.UseOidcProxy();
        await app.StartAsync();
        return (new BffHost(app, clock), clientId, secret);
    }

    internal async Task<HttpResponseMessage> LoginAsync(Browser browser, bool withIdentitySession = true)
    {
        var login = await browser.SendAsync(HttpMethod.Get, "/.auth/login", csrf: false);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, login.Headers.Location);
        if (withIdentitySession)
        {
            var session = await Client.PostAsync("/test/session", null);
            request.Headers.Add("Cookie", session.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        }
        var authorization = await Client.SendAsync(request);
        if (authorization.StatusCode != HttpStatusCode.Redirect) return authorization;
        return await browser.SendAsync(HttpMethod.Get, authorization.Headers.Location!.PathAndQuery, csrf: false);
    }

    internal static async Task<AuthenticationTicket> ReadTicketAsync(BffHost host, Browser browser)
    {
        var cookie = new CookieContainer();
        cookie.SetCookies(BffHost.Origin, browser.Cookies);
        var options = host.App.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(BffConstants.CookieScheme);
        var reference = options.TicketDataFormat.Unprotect(cookie.GetCookies(BffHost.Origin)[options.Cookie.Name!]!.Value)!;
        return (await host.Store.ReadAsync(reference.Principal.FindFirstValue(BffConstants.SessionClaim)!, default))!.State.ReadTicket();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null) await App.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private sealed class CompatibilityDbContext(DbContextOptions<CompatibilityDbContext> options) : DbContext(options);
    private sealed class OpenIddictEchoFactory : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(new EchoHandler());
        private sealed class EchoHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token = request.Headers.Authorization?.Parameter }) });
        }
    }
}

public sealed class OpenIddictCompatibilityTests(OpenIddictFixture identity) : IClassFixture<OpenIddictFixture>
{
    [Fact]
    public async Task DiscoveryCodePkceAndSignedTokensKeepTheIdentityResourceAndTenantContract()
    {
        var (host, clientId, _) = await identity.CreateBffAsync();
        await using var dispose = host;
        var browser = new Browser(host);
        Assert.Equal(HttpStatusCode.Redirect, (await identity.LoginAsync(browser)).StatusCode);
        var me = await browser.SendAsync(HttpMethod.Get, "/.auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var claims = (await me.Content.ReadFromJsonAsync<Dictionary<string, string[]>>())!;
        Assert.Equal([OpenIddictFixture.Subject], claims["sub"]);
        Assert.Equal(["mbr"], claims["tenant_id"]);
        Assert.False(claims.ContainsKey("role")); // BR.Identity deliberately puts roles only in access tokens.
        var ticket = await OpenIddictFixture.ReadTicketAsync(host, browser);
        var access = new JsonWebToken(ticket.Properties.GetTokenValue("access_token")!);
        Assert.Equal("at+jwt", access.Typ);
        Assert.Contains("beetroute-api", access.Audiences);
        Assert.Equal("mbr", access.GetClaim("tenant_id").Value);
        Assert.Equal("manager", access.GetClaim("role").Value);
        Assert.Contains(OpenIddictFixture.Scope, access.GetClaim("scope").Value.Split(' '));
        var id = new JsonWebToken(ticket.Properties.GetTokenValue("id_token")!);
        Assert.Contains(clientId, id.Audiences);
        Assert.DoesNotContain(id.Claims, c => c.Type == "role");
    }

    [Fact]
    public async Task ConcurrentRefreshUsesTheTenantCompatibleClientAuthenticationAndRotatesStoredTokens()
    {
        var (host, clientId, secret) = await identity.CreateBffAsync();
        await using var dispose = host;
        var browser = new Browser(host);
        Assert.Equal(HttpStatusCode.Redirect, (await identity.LoginAsync(browser)).StatusCode);
        var previous = await OpenIddictFixture.ReadTicketAsync(host, browser);
        var before = identity.RefreshRequests;
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        var requests = Enumerable.Range(0, 30).Select(_ => browser.SendAsync(HttpMethod.Get, "/api/data")).ToArray();
        Assert.All(await Task.WhenAll(requests), response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(before + 1, identity.RefreshRequests);
        var current = await OpenIddictFixture.ReadTicketAsync(host, browser);
        Assert.NotEqual(previous.Properties.GetTokenValue("refresh_token"), current.Properties.GetTokenValue("refresh_token"));
        var replay = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = secret, ["grant_type"] = "refresh_token",
            ["refresh_token"] = previous.Properties.GetTokenValue("refresh_token")!
        }));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_grant", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutRevokesTheRefreshTokenAndSuppliesClientIdAndValidTenantIdTokenHint(bool nativeForm)
    {
        var (host, clientId, secret) = await identity.CreateBffAsync();
        await using var dispose = host;
        var browser = new Browser(host);
        Assert.Equal(HttpStatusCode.Redirect, (await identity.LoginAsync(browser)).StatusCode);
        var ticket = await OpenIddictFixture.ReadTicketAsync(host, browser);
        var before = identity.RevocationRequests;
        HttpContent? content = null;
        if (nativeForm)
        {
            var antiforgery = await browser.SendAsync(HttpMethod.Get, "/.auth/antiforgery");
            Assert.Equal(HttpStatusCode.OK, antiforgery.StatusCode);
            var tokens = await antiforgery.Content.ReadFromJsonAsync<JsonElement>();
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [tokens.GetProperty("formFieldName").GetString()!] = tokens.GetProperty("requestToken").GetString()!
            });
        }
        var logout = await browser.SendAsync(HttpMethod.Post, "/.auth/end-session", csrf: !nativeForm, content: content);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(before + 1, identity.RevocationRequests);
        var revoked = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = secret, ["grant_type"] = "refresh_token",
            ["refresh_token"] = ticket.Properties.GetTokenValue("refresh_token")!
        }));
        Assert.Equal(HttpStatusCode.BadRequest, revoked.StatusCode);
        Assert.Equal("invalid_grant", (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var remote = await identity.Client.GetAsync(logout.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, remote.StatusCode);
        Assert.Equal("https://app.example/.auth/signout-callback", remote.Headers.Location!.GetLeftPart(UriPartial.Path));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, "/.auth/me")).StatusCode);
    }

    [Fact]
    public async Task IdentityWithoutLoginUiReturns401InsteadOfCompletingAnAnonymousCodeFlow()
    {
        var (host, _, _) = await identity.CreateBffAsync();
        await using var dispose = host;
        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.LoginAsync(new Browser(host), withIdentitySession: false)).StatusCode);
    }

    [Fact]
    public async Task ExistingPublicSpaClientCannotBeUsedAsAConfidentialBffClient()
    {
        var (host, _, _) = await identity.CreateBffAsync(publicClient: true);
        await using var dispose = host;
        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.LoginAsync(new Browser(host))).StatusCode);
    }

    [Fact]
    public async Task ObsoleteColonScopeIsRejectedByTheActualAuthorizationServer()
    {
        var (host, _, _) = await identity.CreateBffAsync(requestedScope: "profile-service:fullaccess");
        await using var dispose = host;
        var response = await identity.LoginAsync(new Browser(host));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_scope", await response.Content.ReadAsStringAsync());
    }
}
