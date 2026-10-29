using System.Net;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class SessionBoundaryTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Proxy_CorruptRedisRecord_RejectsAndRemovesTheRecord()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        var server = infrastructure.Connection.GetServer(infrastructure.Connection.GetEndPoints().Single());
        var key = server.Keys(pattern: "test-spa:testing:" + host.KeyNamespace + ":spa:sessions:*").Single();
        await infrastructure.Connection.GetDatabase().StringSetAsync(key, "invalid-protected-record");

        // Act
        using var first = await browser.ApiAsync(cancellationToken: ct);
        using var repeated = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        repeated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await infrastructure.Connection.GetDatabase().KeyExistsAsync(key)).ShouldBeFalse();
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Proxy_RedisConnectionUnavailable_FailsClosedWithoutSendingToApi()
    {
        // Arrange
        using var connection = await infrastructure.ConnectRedisAsync();
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);

        // Act: pause the real server, then resume it even if the assertion path fails.
        // This collection is nonparallel; the shared container has no concurrent tests.
        HttpResponseMessage unavailable;
        int forwardedWhilePaused;
        await infrastructure.Redis.PauseAsync(TestContext.Current.CancellationToken);
        try
        {
            unavailable = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);
            forwardedWhilePaused = identity.BackendRequests.Count;
        }
        finally
        {
            // Cleanup must resume the shared server even if the test was cancelled.
            await infrastructure.Redis.UnpauseAsync(CancellationToken.None);
        }
        using var response = unavailable;
        using var recovered = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        forwardedWhilePaused.ShouldBe(0);
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Proxy_CookieFromAnotherNamespace_CannotAuthenticateTheSession()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var first = identity.CreateBff("first-app");
        await using var second = identity.CreateBff("second-app");
        using var browser = first.CreateBrowser();
        using var other = second.CreateBrowser(handleCookies: false);
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        other.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("__Host-Http-oidcproxy.spa.session=")).Split(';')[0]);

        // Act
        using var crossNamespace = await other.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var original = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        crossNamespace.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        original.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.BackendRequests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(481)]
    public async Task Proxy_ExpiredIdleOrAbsoluteSession_DoesNotRefreshCredentials(int minutes)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);

        // Act
        host.Clock.Advance(TimeSpan.FromMinutes(minutes));
        using var response = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(0);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_ReplayedCallback_DoesNotRedeemTheAuthorizationCodeAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var start = await browser.GetAsync(OidcProxyRoutes.Login, ct);
        using var authorization = await identityBrowser.GetAsync(start.Headers.Location, ct);
        var callback = authorization.Headers.Location!.PathAndQuery;
        using var login = await browser.GetAsync(callback, ct);

        // Act
        using var replay = await browser.GetAsync(callback, ct);
        using var current = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.CodeRequests.ShouldBe(1);
    }

    [Fact]
    public async Task Logout_GetOrMissingAntiforgery_DoesNotEndTheSession()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);

        // Act
        using var get = await browser.GetAsync(OidcProxyRoutes.Logout, ct);
        using var post = await browser.PostAsync(OidcProxyRoutes.Logout, new FormUrlEncodedContent([]), ct);
        using var stillActive = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        get.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        post.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stillActive.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RevokedTokens.ShouldBeEmpty();
    }
}
