using System.Net;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class RefreshLogoutTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Refresh_RedisUnavailableAfterRedemption_RevokesNewTokenAndInvalidatesOnRecovery()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        var owner = browser.ApiAsync(cancellationToken: ct);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Act: fail persistence after the provider has consumed the rotating token.
        await infrastructure.Redis.PauseAsync(ct);
        try
        {
            identity.ReleaseRefresh.SetResult();
            await identity.RevocationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            // Shared-container cleanup must run after test cancellation too.
            await infrastructure.Redis.UnpauseAsync(CancellationToken.None);
        }
        using var failed = await owner;
        using var recovered = await browser.ApiAsync(cancellationToken: ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        recovered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(1);
        identity.ActiveRefreshTokens.ShouldBe(0);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refresh_TwoReplicasAndConcurrentRequests_RedeemsTheRefreshTokenOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var secondConnection = await infrastructure.ConnectRedisAsync();
        var scope = Guid.NewGuid().ToString("N");
        await using var first = identity.CreateBff(scope);
        await using var second = identity.CreateBff(scope, secondConnection);
        using var browser = first.CreateBrowser();
        using var replica = second.CreateBrowser(handleCookies: false);
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-Http-oidcproxy.session=")).Split(';')[0];
        replica.DefaultRequestHeaders.Add("Cookie", cookie);
        first.Clock.Advance(TimeSpan.FromSeconds(100));
        second.Clock.Advance(TimeSpan.FromSeconds(100));

        // Act: hold the real HTTP response after redemption so both replicas compete.
        var owner = browser.ApiAsync(cancellationToken: ct);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var requests = Enumerable.Range(0, 50).Select(index => (index % 2 == 0 ? browser : replica).ApiAsync(cancellationToken: ct)).ToArray();
        identity.ReleaseRefresh.SetResult();
        var responses = await Task.WhenAll(requests.Prepend(owner));

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
        identity.BackendRequests.ShouldAllBe(request => request.Headers!["Authorization"].Single() == "Bearer " + identity.LastAccessToken);
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_PendingRefresh_RemovesTheSessionAndRevokesLateCredentials(bool nativeForm)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        host.Clock.Advance(TimeSpan.FromSeconds(100));

        // Act: the refresh response is held after issuing the rotated credential.
        var owner = browser.ApiAsync(cancellationToken: ct);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        using var logout = await browser.LogoutAsync(nativeForm, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        identity.ReleaseRefresh.SetResult();
        using var refreshedRequest = await owner;
        using var after = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        refreshedRequest.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RevokedTokens.ShouldContain(identity.LastRefreshToken);
        identity.ActiveRefreshTokens.ShouldBe(0);
        identity.RefreshRequests.ShouldBe(1);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Logout_CommittedRefresh_RevokesTheCurrentTokenSet()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        using var refreshed = await browser.ApiAsync(cancellationToken: ct);
        var currentToken = identity.LastRefreshToken;

        // Act
        using var logout = await browser.LogoutAsync(cancellationToken: ct);
        using var after = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        identity.RevokedTokens.ShouldContain(currentToken);
        identity.ActiveRefreshTokens.ShouldBe(0);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("malformed")]
    public async Task Refresh_ErrorOrAmbiguousResponse_EndsSessionWithoutRedemptionRetry(string failure)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        identity.TokenFailure = failure;
        host.Clock.Advance(TimeSpan.FromSeconds(100));

        // Act
        using var first = await browser.ApiAsync(cancellationToken: ct);
        using var next = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        first.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.ServiceUnavailable);
        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(1);
        identity.BackendRequests.ShouldBeEmpty();
        identity.ActiveRefreshTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_DisconnectedOwner_DoesNotLoseTheRotatedCredential()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act: cancel only after the server has redeemed the rotating refresh token.
        var owner = browser.ApiAsync(cancellationToken: disconnected.Token);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        disconnected.Cancel();
        var waiter = browser.ApiAsync(cancellationToken: ct);
        identity.ReleaseRefresh.SetResult();
        using var completed = await waiter;

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await Should.ThrowAsync<OperationCanceledException>(async () => await owner);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
        identity.ActiveRefreshTokens.ShouldBe(1);
    }
}
