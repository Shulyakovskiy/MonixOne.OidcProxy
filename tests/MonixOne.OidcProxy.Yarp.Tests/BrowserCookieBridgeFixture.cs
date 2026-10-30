using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Types;
using WireMock.Util;
using Yarp.ReverseProxy.Configuration;

namespace MonixOne.OidcProxy.Yarp.Tests;

/// <summary>
/// Runs the actual package on HTTPS Kestrel with local OIDC/backend/provider
/// fixtures. Chromium resolves test origins to bound loopback ports and trusts
/// only the fixture's certificate. No external GW, Identity or Google is used.
/// </summary>
internal sealed class BrowserCookieBridgeFixture : IAsyncDisposable
{
    internal const string App = "https://admin.suite.test";
    internal const string Sibling = "https://untrusted.suite.test";
    internal const string Provider = "https://provider.external.test";
    internal const string Start = "/calendar-api/connections/google/start";
    internal const string Callback = "/calendar-api/connections/google/callback";
    internal const string State = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Policy = "calendar";
    internal string CallbackUrl =>
        App + "/admin" + Callback + "?state=" + State + "&code=test-only-code";
    internal OidcWireMock Identity { get; }
    internal BffApplicationFactory Host { get; }
    internal IBrowser Browser { get; private set; } = null!;
    internal int Connections => Volatile.Read(ref _connections);
    private int _connections;
    private int _pending;
    private IPlaywright? _playwright;

    private BrowserCookieBridgeFixture(InfrastructureFixture infrastructure, bool explicitPolicy)
    {
        Identity = new OidcWireMock(infrastructure);
        Host = Identity.CreateBff();
        Host.PublicOrigin = new Uri(App);
        Host.PathBase = "/admin";
        Host.ConfigureClientOptions = options =>
        {
            options.Routes.Clear();
            options.AutoBindRoutes = true;
            options.ForwardCookies = !explicitPolicy;
            if (explicitPolicy)
            {
                options.CookiePolicies.Add(
                    Policy,
                    new()
                    {
                        ClusterId = "api",
                        Names = ["calendar_google_state"],
                        Path = Callback,
                    }
                );
                options.Routes.Add("calendar", new() { CookiePolicy = Policy });
                options.Routes.Add(
                    "callback",
                    new() { AllowAnonymous = true, CookiePolicy = Policy }
                );
                options.Routes.Add("spa", new() { AllowAnonymous = true });
            }
        };
        Host.ConfigureRoutes = routes =>
        {
            routes.Clear();
            routes.Add(
                new RouteConfig
                {
                    RouteId = "calendar",
                    ClusterId = "api",
                    Match = new()
                    {
                        Hosts = [new Uri(App).Authority],
                        Path = "/calendar-api/{**all}",
                    },
                }
            );
            routes.Add(
                new RouteConfig
                {
                    RouteId = "callback",
                    ClusterId = "api",
                    AuthorizationPolicy = "anonymous",
                    Match = new()
                    {
                        Hosts = [new Uri(App).Authority],
                        Path = Callback,
                        Methods = ["GET"],
                    },
                }
            );
            routes.Add(
                new RouteConfig
                {
                    RouteId = "spa",
                    ClusterId = "api",
                    AuthorizationPolicy = "anonymous",
                    Match = new() { Hosts = [new Uri(App).Authority], Path = "/{**all}" },
                }
            );
        };
        Host.UseKestrel(options =>
            options.Listen(
                IPAddress.Loopback,
                0,
                endpoint => endpoint.UseHttps(infrastructure.Certificate)
            )
        );

        // The backend owns the OAuth transaction and consumes it only after
        // cookie validation, matching the Calendar callback's contract.
        Identity
            .Server.Given(Request.Create().WithPath(Start).UsingPost())
            .RespondWith(
                Response
                    .Create()
                    .WithCallback(_ =>
                    {
                        Interlocked.Exchange(ref _pending, 1);
                        var response = Text(
                            "{\"state\":\"" + State + "\"}",
                            contentType: "application/json"
                        );
                        response.AddHeader(
                            "Set-Cookie",
                            $"calendar_google_state={State}; Path={Callback}; Secure; HttpOnly; SameSite=Lax; Max-Age=600"
                        );
                        return response;
                    })
            );
        Identity
            .Server.Given(Request.Create().WithPath(Callback).UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithCallback(request =>
                    {
                        var cookie = request
                            .Headers?.FirstOrDefault(header =>
                                header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                            )
                            .Value?.Single();
                        var valid =
                            cookie == "calendar_google_state=" + State
                            && QueryHelpers.ParseQuery(new Uri(request.Url).Query)["state"] == State
                            && Interlocked.CompareExchange(ref _pending, 0, 1) == 1;
                        if (valid)
                            Interlocked.Increment(ref _connections);
                        var response = Text(
                            valid ? "connected" : "invalid correlation",
                            valid ? 200 : 400
                        );
                        response.AddHeader(
                            "Set-Cookie",
                            $"calendar_google_state=; Path={Callback}; Max-Age=0; Secure; HttpOnly; SameSite=Lax"
                        );
                        return response;
                    })
            );
        Identity
            .Server.Given(Request.Create().WithPath("/calendar-api/directory").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("{\"items\":[]}")
            );
        Identity
            .Server.Given(Request.Create().WithPath("/").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "text/html")
                    .WithBody("<!doctype html><title>Calendar</title><h1>Calendar</h1>")
            );
        Identity
            .Server.Given(Request.Create().WithPath("/provider").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "text/html")
                    .WithBody(
                        $"<!doctype html><title>Test provider</title><a href=\"{WebUtility.HtmlEncode(CallbackUrl)}\">Complete calendar connection</a>"
                    )
            );
        Identity
            .Server.Given(Request.Create().WithPath("/attack").UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithCallback(request =>
                    {
                        var query = QueryHelpers.ParseQuery(new Uri(request.Url).Query);
                        var injected =
                            $"{query["name"]}={State}; Domain=suite.test; Path=/; Secure; SameSite=Lax";
                        var script =
                            query.TryGetValue("script", out var scriptValue)
                            && scriptValue == "true";
                        var response = Text(
                            script
                                ? "<!doctype html><script>document.cookie="
                                    + System.Text.Json.JsonSerializer.Serialize(injected)
                                    + ";</script>"
                                : "<!doctype html><title>Sibling origin</title>"
                        );
                        if (!script)
                            response.AddHeader("Set-Cookie", injected + "; HttpOnly");
                        return response;
                    })
            );
    }

    internal static async Task<BrowserCookieBridgeFixture> StartAsync(
        InfrastructureFixture infrastructure,
        bool explicitPolicy
    )
    {
        var fixture = new BrowserCookieBridgeFixture(infrastructure, explicitPolicy);
        try
        {
            fixture.Host.StartServer();
            var address = fixture
                .Host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            var port = new Uri(address).Port;
            var providerPort = fixture.Identity.Origin.Port;
            var pin = Convert.ToBase64String(
                SHA256.HashData(infrastructure.Certificate.PublicKey.ExportSubjectPublicKeyInfo())
            );
            fixture._playwright = await Playwright.CreateAsync();
            fixture.Browser = await fixture._playwright.Chromium.LaunchAsync(
                new()
                {
                    Headless = true,
                    Args =
                    [
                        "--no-proxy-server",
                        "--ignore-certificate-errors-spki-list=" + pin,
                        $"--host-resolver-rules=MAP admin.suite.test:443 127.0.0.1:{port}, MAP untrusted.suite.test:443 127.0.0.1:{providerPort}, MAP provider.external.test:443 127.0.0.1:{providerPort}",
                    ],
                }
            );
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal async Task<IBrowserContext> NewContextAsync()
    {
        var context = await Browser.NewContextAsync();
        context.SetDefaultTimeout(10000);
        // Unexpected destinations fail locally instead of relying on internet access.
        await context.RouteAsync(
            "**/*",
            route =>
                new Uri(route.Request.Url).Host
                    is "admin.suite.test"
                        or "untrusted.suite.test"
                        or "provider.external.test"
                        or "localhost"
                    ? route.ContinueAsync()
                    : route.AbortAsync()
        );
        return context;
    }

    internal async Task<int> LoginAndStartCalendarAsync(IPage page)
    {
        await page.GotoAsync(App + "/admin/.auth/login?returnUrl=%2Fadmin%2F");
        await page.WaitForURLAsync(App + "/admin/");
        return await page.EvaluateAsync<int>(
            "async path => (await fetch(path, { method: 'POST', headers: { 'X-CSRF': '1' } })).status",
            "/admin" + Start
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        _playwright?.Dispose();
        await Host.DisposeAsync();
        Identity.Dispose();
    }

    private static ResponseMessage Text(
        string body,
        int status = 200,
        string contentType = "text/html; charset=utf-8"
    )
    {
        var response = new ResponseMessage
        {
            StatusCode = status,
            BodyData = new BodyData { BodyAsString = body, DetectedBodyType = BodyType.String },
        };
        response.AddHeader("Content-Type", contentType);
        return response;
    }
}
