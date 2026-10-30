using System.Net;
using Microsoft.Net.Http.Headers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Yarp.ReverseProxy.Configuration;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class AutomaticApplicationCookieTests(InfrastructureFixture infrastructure)
{
    private const string Start = "/connections/start";
    private const string Callback = "/connections/callback";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cookies_WithoutProviderPolicy_RoundTripAndClearUsingBackendPath(
        bool backendIncludesPathBase
    )
    {
        // Arrange: no provider names, cookie names, or cookie paths are configured in the BFF.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        var backendCookiePath = (backendIncludesPathBase ? "/admin" : "") + Callback;
        identity
            .Server.Given(Request.Create().WithPath(Start).UsingPost())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader(
                        "Set-Cookie",
                        "connector_state=opaque-state; Domain=backend.example; Path="
                            + backendCookiePath
                            + "; Max-Age=600; SameSite=Lax"
                    )
            );
        identity
            .Server.Given(
                Request
                    .Create()
                    .WithPath(Callback)
                    .UsingGet()
                    .WithHeader("Cookie", "connector_state=opaque-state")
            )
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader(
                        "Set-Cookie",
                        "connector_state=; Path=" + backendCookiePath + "; Max-Age=0"
                    )
            );
        await using var host = identity.CreateBff();
        ConfigureApplication(host);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin" + Start);
        request.Headers.Add("X-CSRF", "1");

        // Act
        using var started = await browser.SendAsync(request, ct);
        using var callbackRequest = new HttpRequestMessage(HttpMethod.Get, "/admin" + Callback);
        callbackRequest.Headers.Add("Origin", "https://provider.example");
        callbackRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var consumed = await browser.SendAsync(callbackRequest, ct);
        using var replay = await browser.GetAsync("/admin" + Callback, ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        consumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = SetCookieHeaderValue.Parse(started.Headers.GetValues("Set-Cookie").Single());
        issued.Name.ToString().ShouldNotBe("connector_state");
        issued.Domain.HasValue.ShouldBeFalse();
        issued.Name.ToString().ShouldStartWith("__Host-Http-");
        issued.Path.ToString().ShouldBe("/");
        issued.Secure.ShouldBeTrue();
        issued.HttpOnly.ShouldBeTrue();
        issued.MaxAge.ShouldBe(TimeSpan.FromMinutes(10));
        var deleted = SetCookieHeaderValue.Parse(consumed.Headers.GetValues("Set-Cookie").Single());
        deleted.Name.ShouldBe(issued.Name);
        deleted.Path.ShouldBe(issued.Path);
        deleted.MaxAge.ShouldBe(TimeSpan.Zero);
        replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var callback = identity
            .Server.LogEntries.First(entry => entry.RequestMessage?.Path == Callback)
            .RequestMessage!;
        callback.Headers!.Keys.ShouldNotContain(name =>
            name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Cookies_AnotherClusterCannotReceiveTheWrappedCookie()
    {
        // Arrange: manually send the cookie despite browser Path rules to verify server-side cluster isolation.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity
            .Server.Given(Request.Create().WithPath(Start).UsingPost())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Set-Cookie", "application_state=opaque; Path=/")
            );
        identity
            .Server.Given(Request.Create().WithPath("/other").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        await using var host = identity.CreateBff();
        ConfigureApplication(host);
        host.ConfigureClusters = clusters => clusters.Add(clusters[0] with { ClusterId = "other" });
        var configureRoutes = host.ConfigureRoutes;
        host.ConfigureRoutes = routes =>
        {
            configureRoutes!(routes);
            routes.Add(
                new RouteConfig
                {
                    RouteId = "other",
                    ClusterId = "other",
                    AuthorizationPolicy = "anonymous",
                    Match = new()
                    {
                        Hosts = [BffApplicationFactory.Origin.Authority],
                        Path = "/other",
                    },
                }
            );
        };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin" + Start);
        request.Headers.Add("X-CSRF", "1");
        using var started = await browser.SendAsync(request, ct);
        var wrapped = started.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var otherBrowser = host.CreateBrowser(handleCookies: false);
        otherBrowser.DefaultRequestHeaders.Add("Cookie", wrapped);

        // Act
        using var response = await otherBrowser.GetAsync("/admin/other", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = identity
            .Server.LogEntries.Single(entry => entry.RequestMessage?.Path == "/other")
            .RequestMessage!;
        received.Headers!.Keys.ShouldNotContain(name =>
            name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("//outside.example/")]
    [InlineData("/safe/../outside")]
    public async Task Cookies_InvalidBackendPath_FailsClosed(string path)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity
            .Server.Given(Request.Create().WithPath(Callback).UsingGet())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Set-Cookie", "state=opaque; Path=" + path)
            );
        await using var host = identity.CreateBff();
        ConfigureApplication(host);
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync("/admin" + Callback, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Routes_ProviderLikeQueryDoesNotMakeAProtectedRouteAnonymous()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        ConfigureApplication(host);
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.ApiAsync(
            "/admin/api/status?code=opaque&state=opaque",
            ct
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cookies_ForeignOriginCannotUseCookiesOnAnonymousWildcardRoute()
    {
        // Arrange: bypass browser SameSite rules to verify the gateway boundary.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity
            .Server.Given(Request.Create().WithPath(Start).UsingPost())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Set-Cookie", "application_session=opaque; Path=/; SameSite=None")
            );
        identity
            .Server.Given(Request.Create().WithPath("/public/data").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        await using var host = identity.CreateBff();
        ConfigureApplication(host);
        var configureRoutes = host.ConfigureRoutes;
        host.ConfigureRoutes = routes =>
        {
            configureRoutes!(routes);
            routes.Add(
                new RouteConfig
                {
                    RouteId = "public",
                    ClusterId = "api",
                    AuthorizationPolicy = "anonymous",
                    Match = new()
                    {
                        Hosts = [BffApplicationFactory.Origin.Authority],
                        Path = "/public/{**path}",
                        Methods = ["GET"],
                    },
                }
            );
        };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/admin" + Start);
        startRequest.Headers.Add("X-CSRF", "1");
        using var started = await browser.SendAsync(startRequest, ct);
        var wrapped = started.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var foreignBrowser = host.CreateBrowser(handleCookies: false);
        foreignBrowser.DefaultRequestHeaders.Add("Cookie", wrapped);
        foreignBrowser.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        foreignBrowser.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");

        // Act
        using var response = await foreignBrowser.GetAsync("/admin/public/data", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = identity
            .Server.LogEntries.Single(entry => entry.RequestMessage?.Path == "/public/data")
            .RequestMessage!;
        received.Headers!.Keys.ShouldNotContain(name =>
            name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Theory]
    [InlineData("/connections/callback", true)]
    [InlineData("/connections/callback/complete", true)]
    [InlineData("/connections/callback-suffix", false)]
    [InlineData("/connections/Callback", false)]
    [InlineData("/connections/elsewhere", false)]
    public async Task Cookies_RootedBrowserCookie_PreservesBackendPathBoundary(
        string path,
        bool forwarded
    )
    {
        // Arrange: __Host cookies are sent to every browser path. Forwarding
        // must retain RFC cookie-path boundaries, including case and segments.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity
            .Server.Given(Request.Create().WithPath(Start).UsingPost())
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(200)
                    .WithHeader("Set-Cookie", "state=opaque; Path=" + Callback)
            );
        identity
            .Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        await using var host = identity.CreateBff();
        ConfigureApplication(host);
        var configured = host.ConfigureRoutes!;
        host.ConfigureRoutes = routes =>
        {
            configured(routes);
            routes.Add(
                new RouteConfig
                {
                    RouteId = "connections",
                    ClusterId = "api",
                    Match = new()
                    {
                        Hosts = [BffApplicationFactory.Origin.Authority],
                        Path = "/connections/{**all}",
                    },
                }
            );
        };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin" + Start);
        request.Headers.Add("X-CSRF", "1");
        using var started = await browser.SendAsync(request, ct);

        // Act
        using var response = await browser.ApiAsync("/admin" + path, cancellationToken: ct);
        var received = identity
            .Server.LogEntries.Single(entry => entry.RequestMessage?.Path == path)
            .RequestMessage!;

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        if (forwarded)
            received.Headers!["Cookie"].Single().ShouldBe("state=opaque");
        else
            received.Headers!.Keys.ShouldNotContain(name =>
                name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            );
    }

    private static void ConfigureApplication(BffApplicationFactory host)
    {
        host.PathBase = "/admin";
        host.ConfigureClientOptions = options =>
        {
            options.Routes.Clear();
            options.AutoBindRoutes = true;
            options.ForwardCookies = true;
        };
        host.ConfigureRoutes = routes =>
        {
            routes.Add(
                new RouteConfig
                {
                    RouteId = "start",
                    ClusterId = "api",
                    Match = new()
                    {
                        Hosts = [BffApplicationFactory.Origin.Authority],
                        Path = Start,
                        Methods = ["POST"],
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
                        Hosts = [BffApplicationFactory.Origin.Authority],
                        Path = Callback,
                        Methods = ["GET"],
                    },
                }
            );
        };
    }
}
