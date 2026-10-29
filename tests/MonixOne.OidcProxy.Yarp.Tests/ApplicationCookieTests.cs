using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Yarp.ReverseProxy.Configuration;
using AppSameSite = Microsoft.AspNetCore.Http.SameSiteMode;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class ApplicationCookieTests(InfrastructureFixture infrastructure)
{
    private const string Start = "/calendar-api/connections/google/start";
    private const string Callback = "/calendar-api/connections/google/callback";
    private const string StateCookie = "calendar_google_state";

    [Fact]
    public async Task Callback_AllowedCalendarStateCookie_RestoresOnlyItsOriginalNameAndValue()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Set-Cookie", StateCookie + "=opaque-state; Domain=backend.example; Path=/; Max-Age=3600; SameSite=None")
            .WithBody("authorize-url"));
        identity.Server.Given(Request.Create().WithPath(Callback).UsingGet().WithHeader("Cookie", StateCookie + "=opaque-state"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("linked"));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");

        // Act
        using var started = await StartAsync(browser, ct);
        using var callback = await browser.GetAsync("/admin" + Callback + "?state=opaque-state&code=test-code", ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = SetCookieHeaderValue.Parse(started.Headers.GetValues("Set-Cookie").Single());
        cookie.Name.ToString().ShouldNotBe(StateCookie);
        cookie.Value.ToString().ShouldBe("opaque-state");
        cookie.Domain.HasValue.ShouldBeFalse();
        cookie.Path.ToString().ShouldBe("/admin" + Callback);
        cookie.Secure.ShouldBeTrue();
        cookie.HttpOnly.ShouldBeTrue();
        cookie.SameSite.ShouldBe(Microsoft.Net.Http.Headers.SameSiteMode.Lax);
        cookie.MaxAge.ShouldBe(TimeSpan.FromMinutes(10));
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);
        callback.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var received = identity.Server.LogEntries.Single(entry => entry.RequestMessage?.Path == Callback).RequestMessage!;
        received.Headers!["Cookie"].Single().ShouldBe(StateCookie + "=opaque-state");
        received.Headers!.Keys.ShouldNotContain(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        received.Query!["state"].Single().ShouldBe("opaque-state");
    }

    [Fact]
    public async Task Callback_BackendClearsCookie_DeletesTheSameNamespacedCookie()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Set-Cookie", StateCookie + "=one-use-state; Path=/; Max-Age=600"));
        identity.Server.Given(Request.Create().WithPath(Callback).UsingGet().WithHeader("Cookie", StateCookie + "=one-use-state"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Set-Cookie", StateCookie + "=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT"));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var started = await StartAsync(browser, ct);

        // Act
        using var consumed = await browser.GetAsync("/admin" + Callback, ct);
        using var replay = await browser.GetAsync("/admin" + Callback, ct);

        // Assert
        consumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = SetCookieHeaderValue.Parse(started.Headers.GetValues("Set-Cookie").Single());
        var deleted = SetCookieHeaderValue.Parse(consumed.Headers.GetValues("Set-Cookie").Single());
        deleted.Name.ShouldBe(issued.Name);
        deleted.Path.ShouldBe(issued.Path);
        deleted.MaxAge.ShouldBe(TimeSpan.Zero);
        replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Callback_DuplicateNamespacedCookie_RejectsBeforeForwarding()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Set-Cookie", StateCookie + "=state; Path=/"));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        using var browser = host.CreateBrowser();
        using var replay = host.CreateBrowser(handleCookies: false);
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var started = await StartAsync(browser, ct);
        var name = SetCookieHeaderValue.Parse(started.Headers.GetValues("Set-Cookie").Single()).Name.ToString();
        replay.DefaultRequestHeaders.Add("Cookie", name + "=first; " + name + "=second");

        // Act
        using var callback = await replay.GetAsync("/admin" + Callback, ct);

        // Assert
        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        identity.Server.LogEntries.ShouldNotContain(entry => entry.RequestMessage != null && entry.RequestMessage.Path == Callback);
    }

    [Fact]
    public async Task Proxy_RouteWithoutCookiePolicy_DoesNotForwardTheApplicationCookie()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Set-Cookie", StateCookie + "=state; Path=/"));
        identity.Server.Given(Request.Create().WithPath("/calendar-api/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host, path: "/");
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var started = await StartAsync(browser, ct);

        // Act
        using var response = await browser.ApiAsync("/admin/calendar-api/status", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = identity.Server.LogEntries.Single(entry => entry.RequestMessage?.Path == "/calendar-api/status").RequestMessage!;
        received.Headers!.Keys.ShouldNotContain(key => key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        received.Headers!["Authorization"].Single().ShouldBe("Bearer " + identity.LastAccessToken);
    }

    [Fact]
    public async Task Callback_UnprefixedApplicationCookie_CannotBeInjectedIntoTheBackend()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Callback).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        using var browser = host.CreateBrowser(handleCookies: false);
        browser.DefaultRequestHeaders.Add("Cookie", StateCookie + "=forged; " + OidcProxyDefaults.CookieName("spa") + "=private-bff-cookie");

        // Act
        using var response = await browser.GetAsync("/admin" + Callback, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.Server.LogEntries.Single(entry => entry.RequestMessage?.Path == Callback).RequestMessage!.Headers!
            .Keys.ShouldNotContain(key => key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(".AspNetCore.Correlation.test-id", ".AspNetCore.Correlation.*")]
    [InlineData("__Host-calendar-correlation", "__Host-calendar-correlation")]
    public async Task Callback_DynamicOrHostPrefixedBackendName_SurvivesTheScopedRoundTrip(string name, string allowed)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Set-Cookie", name + "=nonce; Path=/; Secure; HttpOnly; SameSite=None"));
        identity.Server.Given(Request.Create().WithPath(Callback).UsingGet().WithHeader("Cookie", name + "=nonce"))
            .RespondWith(Response.Create().WithStatusCode(200));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host, names: [allowed]);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");

        // Act
        using var started = await StartAsync(browser, ct);
        using var response = await browser.GetAsync("/admin" + Callback, ct);

        // Assert
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Max-Age=3600", 600)]
    [InlineData("Max-Age=120", 120)]
    [InlineData("", null)]
    public async Task Start_BackendLifetime_IsCappedWithoutExtendingOrPersistingSessionCookies(string attribute, int? seconds)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Set-Cookie", StateCookie + "=nonce; Path=/" + (attribute.Length == 0 ? "" : "; " + attribute)));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");

        // Act
        using var response = await StartAsync(browser, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = SetCookieHeaderValue.Parse(response.Headers.GetValues("Set-Cookie").Single());
        cookie.MaxAge.ShouldBe(seconds is { } value ? TimeSpan.FromSeconds(value) : null);
        cookie.Expires.ShouldBeNull();
    }

    [Theory]
    [InlineData("Strict", AppSameSite.Lax, Microsoft.Net.Http.Headers.SameSiteMode.Strict)]
    [InlineData("None", AppSameSite.None, Microsoft.Net.Http.Headers.SameSiteMode.None)]
    [InlineData("", AppSameSite.None, Microsoft.Net.Http.Headers.SameSiteMode.Lax)]
    public async Task Start_SameSitePolicy_DoesNotWeakenTheBackendMode(string original, AppSameSite policy, Microsoft.Net.Http.Headers.SameSiteMode expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Set-Cookie", StateCookie + "=nonce; Path=/" + (original.Length == 0 ? "" : "; SameSite=" + original)));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host, sameSite: policy);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");

        // Act
        using var response = await StartAsync(browser, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SetCookieHeaderValue.Parse(response.Headers.GetValues("Set-Cookie").Single()).SameSite.ShouldBe(expected);
    }

    [Fact]
    public async Task Start_MalformedSetCookie_FailsWithoutReturningCookiesOrSuccessPayload()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Set-Cookie", "not-a-cookie").WithBody("must-not-return-success"));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");

        // Act
        using var response = await StartAsync(browser, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        (await response.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("name*middle")]
    [InlineData("name; Path=/")]
    public async Task Startup_InvalidCookieNamePattern_RejectsConfiguration(string name)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        ConfigureCalendar(host, names: [name]);

        // Act
        var error = Should.Throw<OptionsValidationException>(() => host.CreateBrowser());

        // Assert
        error.Message.ShouldContain("CookiePolicies");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong-cluster")]
    [InlineData("unmanaged")]
    public async Task Startup_InvalidCookieRouteBinding_RejectsConfiguration(string defect)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        var configure = host.ConfigureRoutes!;
        host.ConfigureRoutes = routes =>
        {
            configure(routes);
            var metadata = new Dictionary<string, string> { [OidcProxyDefaults.ClientMetadataKey] = "spa", [OidcProxyDefaults.CookiePolicyMetadataKey] = "calendar-google" };
            if (defect == "unknown") metadata[OidcProxyDefaults.CookiePolicyMetadataKey] = "missing";
            if (defect == "unmanaged") metadata.Remove(OidcProxyDefaults.ClientMetadataKey);
            routes[^1] = routes[^1] with { Metadata = metadata, ClusterId = defect == "wrong-cluster" ? "another" : "api" };
        };

        // Act
        var error = Should.Throw<InvalidOperationException>(() => host.CreateBrowser());

        // Assert
        error.Message.ShouldContain("configuration");
    }

    [Fact]
    public async Task Callback_TwoServicesUseTheSameBackendCookieName_KeepTheirCookiesIsolated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        using var other = new OidcWireMock(infrastructure);
        foreach (var (backend, label) in new[] { (identity, "first"), (other, "second") })
        {
            backend.Server.Given(Request.Create().WithPath("/" + label + "/start").UsingPost()).RespondWith(Response.Create()
                .WithStatusCode(200).WithHeader("Set-Cookie", "state=" + label + "; Path=/; HttpOnly; Secure"));
            backend.Server.Given(Request.Create().WithPath("/" + label + "/callback").UsingGet().WithHeader("Cookie", "state=" + label))
                .RespondWith(Response.Create().WithStatusCode(200));
        }
        await using var host = identity.CreateBff();
        host.PathBase = "/admin";
        host.ConfigureClientOptions = options =>
        {
            options.CookiePolicies["first"] = new() { ClusterId = "api", Names = ["state"], Path = "/" };
            options.CookiePolicies["second"] = new() { ClusterId = "second", Names = ["state"], Path = "/" };
        };
        host.ConfigureClusters = clusters => clusters.Add(new() { ClusterId = "second", Destinations = new Dictionary<string, DestinationConfig>
            { ["primary"] = new() { Address = other.Origin.AbsoluteUri } } });
        host.ConfigureRoutes = routes =>
        {
            foreach (var label in new[] { "first", "second" })
            {
                var metadata = new Dictionary<string, string> { [OidcProxyDefaults.ClientMetadataKey] = "spa", [OidcProxyDefaults.CookiePolicyMetadataKey] = label };
                routes.Add(routes[0] with { RouteId = label + "-start", ClusterId = label == "first" ? "api" : "second", Metadata = metadata,
                    Match = routes[0].Match with { Path = "/" + label + "/start", Methods = ["POST"] } });
                routes.Add(routes[0] with { RouteId = label + "-callback", ClusterId = label == "first" ? "api" : "second", Metadata = metadata,
                    AuthorizationPolicy = "anonymous", Match = routes[0].Match with { Path = "/" + label + "/callback", Methods = ["GET"] } });
            }
        };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");
        using var startARequest = new HttpRequestMessage(HttpMethod.Post, "/admin/first/start");
        using var startBRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/second/start");
        startARequest.Headers.Add("X-CSRF", "1");
        startBRequest.Headers.Add("X-CSRF", "1");

        // Act
        using var startA = await browser.SendAsync(startARequest, ct);
        using var startB = await browser.SendAsync(startBRequest, ct);
        using var callbackA = await browser.GetAsync("/admin/first/callback", ct);
        using var callbackB = await browser.GetAsync("/admin/second/callback", ct);

        // Assert
        startA.StatusCode.ShouldBe(HttpStatusCode.OK);
        startB.StatusCode.ShouldBe(HttpStatusCode.OK);
        SetCookieHeaderValue.Parse(startA.Headers.GetValues("Set-Cookie").Single()).Name
            .ShouldNotBe(SetCookieHeaderValue.Parse(startB.Headers.GetValues("Set-Cookie").Single()).Name);
        callbackA.StatusCode.ShouldBe(HttpStatusCode.OK);
        callbackB.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.Server.LogEntries.Single(entry => entry.RequestMessage?.Path == "/first/callback").RequestMessage!.Headers!["Cookie"].Single().ShouldBe("state=first");
        other.Server.LogEntries.Single(entry => entry.RequestMessage?.Path == "/second/callback").RequestMessage!.Headers!["Cookie"].Single().ShouldBe("state=second");
    }

    [Fact]
    public async Task Login_BackendIssuesTheSameCorrelationName_DoesNotOverwriteThePendingBffCookie()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        ConfigureCalendar(host, path: "/", names: [".AspNetCore.Correlation.*"]);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var original = await browser.LoginAsync(provider, "/admin");
        using var pending = await browser.GetAsync("/admin" + OidcProxyRoutes.Login, ct);
        var correlation = pending.Headers.GetValues("Set-Cookie").Select(value => SetCookieHeaderValue.Parse(value))
            .Single(cookie => cookie.Name.ToString().StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Set-Cookie", correlation.Name + "=forged; Path=/; HttpOnly; Secure"));

        // Act
        using var application = await StartAsync(browser, ct);
        using var authorization = await provider.GetAsync(pending.Headers.Location, ct);
        using var callback = await browser.GetAsync(authorization.Headers.Location, ct);
        using var active = await browser.ApiAsync("/admin" + OidcProxyRoutes.User, ct);

        // Assert
        application.StatusCode.ShouldBe(HttpStatusCode.OK);
        SetCookieHeaderValue.Parse(application.Headers.GetValues("Set-Cookie").Single()).Name.ShouldNotBe(correlation.Name);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        active.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Start_PartitionedCookie_PreservesPartitioningAlongsideMandatoryFlags()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Set-Cookie", StateCookie + "=nonce; Domain=backend.example; Path=/; Secure; HttpOnly; SameSite=None; Partitioned"));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host, sameSite: AppSameSite.None);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");

        // Act
        using var response = await StartAsync(browser, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = SetCookieHeaderValue.Parse(response.Headers.GetValues("Set-Cookie").Single());
        cookie.Extensions.ShouldContain(extension => extension.ToString().Equals("Partitioned", StringComparison.OrdinalIgnoreCase));
        cookie.Domain.HasValue.ShouldBeFalse();
        cookie.Secure.ShouldBeTrue();
        cookie.HttpOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task Start_PositiveMaxAgeAndPastExpires_KeepsTheAuthoritativeLifetime()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        identity.Server.Given(Request.Create().WithPath(Start).UsingPost()).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Set-Cookie", StateCookie + "=nonce; Path=/; Max-Age=600; Expires=Thu, 01 Jan 1970 00:00:00 GMT"));
        await using var host = identity.CreateBff();
        ConfigureCalendar(host);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, "/admin");

        // Act
        using var response = await StartAsync(browser, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SetCookieHeaderValue.Parse(response.Headers.GetValues("Set-Cookie").Single()).MaxAge.ShouldBe(TimeSpan.FromMinutes(10));
    }

    private static void ConfigureCalendar(BffApplicationFactory host, string path = Callback, string[]? names = null, AppSameSite sameSite = AppSameSite.Lax)
    {
        host.PathBase = "/admin";
        host.ConfigureClientOptions = options => options.CookiePolicies["calendar-google"] = new OidcProxyCookiePolicy
        {
            ClusterId = "api", Names = names ?? [StateCookie], Path = path, SameSite = sameSite, MaxAge = TimeSpan.FromMinutes(10)
        };
        host.ConfigureRoutes = routes =>
        {
            routes[0] = routes[0] with { Match = routes[0].Match with { Path = "/calendar-api/{**all}" } };
            var metadata = new Dictionary<string, string> { [OidcProxyDefaults.ClientMetadataKey] = "spa", [OidcProxyDefaults.CookiePolicyMetadataKey] = "calendar-google" };
            routes.Add(routes[0] with { RouteId = "start", Metadata = metadata, Match = routes[0].Match with { Path = Start, Methods = ["POST"] } });
            routes.Add(routes[0] with { RouteId = "callback", Metadata = metadata, AuthorizationPolicy = "anonymous", Match = routes[0].Match with { Path = Callback, Methods = ["GET", "POST"] } });
        };
    }

    private static async Task<HttpResponseMessage> StartAsync(HttpClient browser, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin" + Start);
        request.Headers.Add("X-CSRF", "1");
        return await browser.SendAsync(request, ct);
    }
}
