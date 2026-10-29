using System.Net;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class SpaBoundaryTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Login_CodePkceFlow_CreatesCookieAndKeepsTokensOnTheServer()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(identityBrowser);
        using var me = await browser.ApiAsync("/.auth/me", TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.ShouldBe("/");
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-Http-oidcproxy.spa.session="));
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldNotContain(identity.LastAccessToken);
        cookie.ShouldNotContain(identity.LastRefreshToken);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(identity.LastAccessToken);
    }

    [Fact]
    public async Task Proxy_AnonymousBrowser_Returns401WithoutOidcRedirect()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "https://attacker.example")]
    [InlineData(true, "null")]
    public async Task Proxy_MissingCsrfOrForeignOrigin_DoesNotReachBackend(bool csrf, string? origin)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);

        // Act
        using var response = await browser.ApiAsync(csrf: csrf, origin: origin, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        identity.BackendRequests.ShouldBeEmpty();
        identity.RefreshRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Proxy_BrowserBearerAndCookies_UsesOnlyTheServerAccessToken()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);

        // Act
        using var response = await browser.ApiAsync(bearer: "attacker-token", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var forwarded = identity.BackendRequests.Single();
        forwarded.Headers!["Authorization"].Single().ShouldBe("Bearer " + identity.LastAccessToken);
        forwarded.Headers.Keys.ShouldNotContain(key => key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(identity.LastAccessToken);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("nonce")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    public async Task Login_InvalidIdentityToken_RejectsTheCallback(string defect)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure) { InvalidIdToken = defect };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(identityBrowser);
        using var api = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.ActiveRefreshTokens.ShouldBe(0);
        identity.RevokedTokens.ShouldContain(identity.LastRefreshToken);
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("missing-access-token")]
    [InlineData("token-type")]
    [InlineData("expiration")]
    public async Task Login_InvalidTokenEndpointResponse_RevokesIssuedCredentialsWithoutCreatingSession(string defect)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure) { InvalidTokenResponse = defect };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(identityBrowser);
        using var api = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.ActiveRefreshTokens.ShouldBe(0);
        identity.RevokedTokens.ShouldContain(identity.LastRefreshToken);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_Reauthentication_InvalidatesThePreviousCookieReference()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var replay = host.CreateBrowser(handleCookies: false);
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var first = await browser.LoginAsync(identityBrowser);
        var previousCookie = first.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-Http-oidcproxy.spa.session=")).Split(';')[0];

        // Act
        using var second = await browser.LoginAsync(identityBrowser);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Cookie", previousCookie);
        request.Headers.Add("X-CSRF", "1");
        using var oldSession = await replay.SendAsync(request, TestContext.Current.CancellationToken);
        using var current = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        second.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        oldSession.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.ActiveRefreshTokens.ShouldBe(1);
    }

    [Fact]
    public async Task Login_CrossSiteCallbackWithoutStrictSessionCookie_InvalidatesPreviousReference()
    {
        // Arrange: an external provider callback carries nonce/correlation cookies,
        // but a real browser omits the existing SameSite=Strict session cookie.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var callbackBrowser = host.CreateBrowser(handleCookies: false);
        using var replay = host.CreateBrowser(handleCookies: false);
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var original = await browser.LoginAsync(identityBrowser);
        var originalCookie = original.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("__Host-Http-oidcproxy.spa.session=")).Split(';')[0];
        using var start = await browser.GetAsync(OidcProxyRoutes.Login, ct);
        using var authorization = await identityBrowser.GetAsync(start.Headers.Location, ct);
        callbackBrowser.DefaultRequestHeaders.Add("Cookie", string.Join("; ",
            start.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])));
        replay.DefaultRequestHeaders.Add("Cookie", originalCookie);

        // Act
        using var callback = await callbackBrowser.GetAsync(authorization.Headers.Location!.PathAndQuery, ct);
        using var stale = await replay.ApiAsync(cancellationToken: ct);

        // Assert
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.ActiveRefreshTokens.ShouldBe(1);
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("//attacker.example")]
    public async Task Login_UserSuppliedLandingPage_CannotChangeTheReturnOrigin(string landingPage)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();

        // Act
        using var start = await browser.GetAsync("/.auth/login?landingpage=" + Uri.EscapeDataString(landingPage), TestContext.Current.CancellationToken);
        using var authorization = await identityBrowser.GetAsync(start.Headers.Location, TestContext.Current.CancellationToken);
        using var callback = await browser.GetAsync(authorization.Headers.Location!.PathAndQuery, TestContext.Current.CancellationToken);

        // Assert
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.ShouldBe("/");
    }
}
