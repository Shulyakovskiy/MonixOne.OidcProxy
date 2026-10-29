using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Yarp.ReverseProxy.Configuration;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class MultiHostTests(InfrastructureFixture infrastructure)
{
    private static readonly Uri SecondOrigin = new("https://second.example");

    [Fact]
    public async Task LoginRefreshAndLogout_TwoClientsOnOneOpenIddictServer_KeepGrantsIsolated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new OpenIddictHost(infrastructure);
        await provider.InitializeAsync();
        var firstId = await provider.RegisterClientAsync();
        var secondId = await provider.RegisterClientAsync(origin: SecondOrigin);
        await using var host = new BffApplicationFactory(infrastructure, provider.Origin, firstId, OpenIddictHost.Secret, provider.Origin);
        host.AddClient("second", SecondOrigin, provider.Origin, secondId, OpenIddictHost.Secret, provider.Origin);
        using var browser = host.CreateBrowser();
        using var identity = provider.CreateBrowser();
        using var session = await identity.PostAsync("/test/session", null, ct);
        using var loginA = await browser.LoginAsync(identity);
        using var loginB = await browser.LoginAsync(identity, origin: SecondOrigin);
        host.Clock.Advance(TimeSpan.FromSeconds(100));

        // Act
        var firstRequest = browser.ApiAsync(cancellationToken: ct);
        var secondRequest = browser.ApiAsync(new Uri(SecondOrigin, "/api/status").AbsoluteUri, ct);
        using var firstApi = await firstRequest;
        using var secondApi = await secondRequest;
        using var logout = await browser.LogoutAsync(cancellationToken: ct);
        using var providerLogout = await identity.GetAsync(logout.Headers.Location, ct);
        using var callback = await browser.GetAsync(providerLogout.Headers.Location, ct);
        using var firstAfter = await browser.ApiAsync(cancellationToken: ct);
        using var secondAfter = await browser.ApiAsync(new Uri(SecondOrigin, "/api/status").AbsoluteUri, ct);

        // Assert
        loginA.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        loginB.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        firstApi.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondApi.StatusCode.ShouldBe(HttpStatusCode.OK);
        provider.RefreshRequests[firstId].ShouldBe(1);
        provider.RefreshRequests[secondId].ShouldBe(1);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.ShouldBe("/");
        firstAfter.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        secondAfter.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_TwoOriginsInOneBrowser_UseTheirOwnClientAndBackend()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var first = new OidcWireMock(infrastructure);
        using var second = new OidcWireMock(infrastructure, "second-confidential-client");
        await using var host = first.CreateBff();
        host.AddClient("second", SecondOrigin, second, "/portal");
        using var browser = host.CreateBrowser();
        using var identity = first.CreateIdentityBrowser();

        // Act
        using var loginA = await browser.LoginAsync(identity);
        using var loginB = await browser.LoginAsync(identity, "/portal", origin: SecondOrigin);
        using var apiA = await browser.ApiAsync(cancellationToken: ct);
        using var apiB = await browser.ApiAsync(new Uri(SecondOrigin, "/portal/api/status").AbsoluteUri, ct);

        // Assert
        loginA.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        loginB.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        loginB.Headers.Location!.OriginalString.ShouldBe("/portal/");
        apiA.StatusCode.ShouldBe(HttpStatusCode.OK);
        apiB.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.CodeRequests.ShouldBe(1);
        second.CodeRequests.ShouldBe(1);
        first.BackendRequests.Single().Headers!["Authorization"].Single().ShouldBe("Bearer " + first.LastAccessToken);
        second.BackendRequests.Single().Headers!["Authorization"].Single().ShouldBe("Bearer " + second.LastAccessToken);
    }

    [Fact]
    public async Task Proxy_RenamedCookieFromAnotherClient_RejectsWithoutTokenForwarding()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var first = new OidcWireMock(infrastructure);
        using var second = new OidcWireMock(infrastructure, "second-client");
        await using var host = first.CreateBff();
        host.AddClient("second", SecondOrigin, second);
        using var browser = host.CreateBrowser();
        using var attacker = host.CreateBrowser(handleCookies: false);
        using var identity = first.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identity);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(OidcProxyDefaults.CookieName("spa") + "="))
            .Split(';')[0].Replace(OidcProxyDefaults.CookieName("spa"), OidcProxyDefaults.CookieName("second"), StringComparison.Ordinal);
        attacker.DefaultRequestHeaders.Add("Cookie", cookie);

        // Act
        using var replay = await attacker.ApiAsync(new Uri(SecondOrigin, "/api/status").AbsoluteUri, ct);
        using var original = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        original.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.BackendRequests.ShouldBeEmpty();
        second.RefreshRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Login_CallbackStateMovedToAnotherHost_IsRejectedBeforeCodeRedemption()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var first = new OidcWireMock(infrastructure);
        using var second = new OidcWireMock(infrastructure, "second-client");
        await using var host = first.CreateBff();
        host.AddClient("second", SecondOrigin, second);
        using var browser = host.CreateBrowser();
        using var attacker = host.CreateBrowser(handleCookies: false);
        using var identity = first.CreateIdentityBrowser();
        using var start = await browser.GetAsync(OidcProxyRoutes.Login, ct);
        using var authorization = await identity.GetAsync(start.Headers.Location, ct);
        attacker.DefaultRequestHeaders.Add("Cookie", string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0])));

        // Act
        using var wrongHost = await attacker.GetAsync(new Uri(SecondOrigin, authorization.Headers.Location!.PathAndQuery), ct);
        using var original = await browser.GetAsync(authorization.Headers.Location, ct);

        // Assert
        wrongHost.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        original.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        first.CodeRequests.ShouldBe(1);
        second.CodeRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_OtherClientIsRefreshing_DoesNotInvalidateOrBlockItsSession(bool nativeForm)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var first = new OidcWireMock(infrastructure);
        using var second = new OidcWireMock(infrastructure, "second-client")
        { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = first.CreateBff();
        host.AddClient("second", SecondOrigin, second);
        using var browser = host.CreateBrowser();
        using var identity = first.CreateIdentityBrowser();
        using var loginA = await browser.LoginAsync(identity);
        using var loginB = await browser.LoginAsync(identity, origin: SecondOrigin);
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        var refreshing = browser.ApiAsync(new Uri(SecondOrigin, "/api/status").AbsoluteUri, ct);
        await second.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Act
        using var logout = await browser.LogoutAsync(nativeForm, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        second.ReleaseRefresh.SetResult();
        using var completed = await refreshing;
        using var loggedOut = await browser.ApiAsync(cancellationToken: ct);
        using var unaffected = await browser.ApiAsync(new Uri(SecondOrigin, "/api/status").AbsoluteUri, ct);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        loggedOut.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        unaffected.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.ActiveRefreshTokens.ShouldBe(0);
        second.ActiveRefreshTokens.ShouldBe(1);
        second.RefreshRequests.ShouldBe(1);
        second.RevokedTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refresh_ConcurrentRequestsForTwoClients_RedeemsEachTokenOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var first = new OidcWireMock(infrastructure) { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var second = new OidcWireMock(infrastructure, "second-client") { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = first.CreateBff();
        host.AddClient("second", SecondOrigin, second);
        using var browser = host.CreateBrowser();
        using var identity = first.CreateIdentityBrowser();
        using var loginA = await browser.LoginAsync(identity);
        using var loginB = await browser.LoginAsync(identity, origin: SecondOrigin);
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        var ownerA = browser.ApiAsync(cancellationToken: ct);
        var ownerB = browser.ApiAsync(new Uri(SecondOrigin, "/api/status").AbsoluteUri, ct);
        await Task.WhenAll(first.RefreshStarted.Task, second.RefreshStarted.Task).WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Act
        var followers = Enumerable.Range(0, 20).Select(i => browser.ApiAsync(i % 2 == 0 ? "/api/status" : new Uri(SecondOrigin, "/api/status").AbsoluteUri, ct)).ToArray();
        first.ReleaseRefresh.SetResult();
        second.ReleaseRefresh.SetResult();
        var responses = await Task.WhenAll(followers.Append(ownerA).Append(ownerB));

        // Assert
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        first.RefreshRequests.ShouldBe(1);
        second.RefreshRequests.ShouldBe(1);
        first.BackendRequests.ShouldAllBe(r => r.Headers!["Authorization"].Single() == "Bearer " + first.LastAccessToken);
        second.BackendRequests.ShouldAllBe(r => r.Headers!["Authorization"].Single() == "Bearer " + second.LastAccessToken);
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData("https://unknown.example/.auth/login")]
    [InlineData("https://unknown.example/.auth/login/callback?code=forged&state=forged")]
    [InlineData("https://second.example/.auth/login")]
    public async Task Login_UnknownHostOrOutsideConfiguredMount_DoesNotStartOidc(string url)
    {
        // Arrange
        using var first = new OidcWireMock(infrastructure);
        using var second = new OidcWireMock(infrastructure, "second-client");
        await using var host = first.CreateBff();
        host.AddClient("second", SecondOrigin, second, "/portal");
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync(url, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.Location.ShouldBeNull();
        first.CodeRequests.ShouldBe(0);
        second.CodeRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Proxy_UnboundRouteOnSpaHost_FailsClosed()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureRoutes = routes => routes[0] = routes[0] with { Metadata = null };
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Proxy_ExplicitAnonymousSpaRoute_ForwardsWithoutAnyBrowserCredentials()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureRoutes = routes => routes.Add(routes[0] with { RouteId = "public", Match = new() { Hosts = [BffApplicationFactory.Origin.Authority], Path = "/api/public" }, AuthorizationPolicy = "anonymous" });
        using var browser = host.CreateBrowser();
        browser.DefaultRequestHeaders.Add("Cookie", "input=secret");

        // Act
        using var response = await browser.ApiAsync("/api/public", ct, csrf: false, bearer: "browser-token");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var forwarded = identity.BackendRequests.Single();
        forwarded.Headers!.Keys.ShouldNotContain(k => k.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || k.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }
}
