using System.Net;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class RedisMutationTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Logout_ConfirmedGetDel_DoesNotSendAnotherSessionDelete()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gate = new RedisReplyGate(infrastructure);
        using var connection = await gate.ConnectAsync();
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        var extraDelete = gate.HoldNextSessionReply("DEL");

        // Act
        using var logout = await browser.LogoutAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        extraDelete.Reached.Task.IsCompleted.ShouldBeFalse();
        identity.ActiveRefreshTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_ClaimReplyTimedOut_ConfirmsOperationBeforeRedemption()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gate = new RedisReplyGate(infrastructure);
        using var connection = await gate.ConnectAsync(asyncTimeout: 1000);
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        var claim = gate.HoldNextSessionReply("EVAL");

        // Act: release only when the timeout causes an ownership recovery read.
        var request = browser.ApiAsync(cancellationToken: ct);
        await claim.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await claim.RecoveryRead.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var redeemedBeforeConfirmation = identity.RefreshRequests;
        claim.Release.TrySetResult();
        using var completed = await request;
        using var repeated = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        redeemedBeforeConfirmation.ShouldBe(0);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
    }

    [Fact]
    public async Task Login_SessionCreationReplyTimedOut_RejectsAndRevokesKnownCredentials()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gate = new RedisReplyGate(infrastructure);
        using var connection = await gate.ConnectAsync();
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        var creation = gate.HoldNextSessionReply("SET");

        // Act: an applied but unacknowledged SET can leave only a TTL-bounded record.
        var login = browser.LoginAsync(provider);
        await creation.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        using var failed = await login.WaitAsync(TimeSpan.FromSeconds(10), ct);
        creation.Release.TrySetResult();
        using var after = await browser.ApiAsync(cancellationToken: ct);
        var server = infrastructure.Connection.GetServer(infrastructure.Connection.GetEndPoints().Single());
        var key = server.Keys(pattern: "test-spa:testing:" + host.KeyNamespace + ":spa:sessions:*").Single();
        var ttl = await infrastructure.Connection.GetDatabase().KeyTimeToLiveAsync(key);

        // Assert
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.ActiveRefreshTokens.ShouldBe(0);
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task Login_DisconnectAfterSessionCreation_CleansUpConfirmedRecord()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gate = new RedisReplyGate(infrastructure);
        using var connection = await gate.ConnectAsync();
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var start = await browser.GetAsync(OidcProxyRoutes.Login, ct);
        using var authorization = await provider.GetAsync(start.Headers.Location, ct);
        var creation = gate.HoldNextSessionReply("SET");
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act: cancellation arrives after SET, before its acknowledgement.
        var callback = browser.GetAsync(authorization.Headers.Location, disconnected.Token);
        await creation.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        disconnected.Cancel();
        creation.Release.TrySetResult();
        await Should.ThrowAsync<OperationCanceledException>(async () => await callback);
        await identity.RevocationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var server = infrastructure.Connection.GetServer(infrastructure.Connection.GetEndPoints().Single());

        // Assert
        server.Keys(pattern: "test-spa:testing:" + host.KeyNamespace + ":spa:sessions:*").ShouldBeEmpty();
        identity.ActiveRefreshTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_DisconnectAfterRedisClaim_CompletesServerOwnedRefresh()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gate = new RedisReplyGate(infrastructure);
        using var connection = await gate.ConnectAsync();
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        var claim = gate.HoldNextSessionReply("EVAL");
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act: Redis has committed the claim, but its reply has not reached BFF.
        var request = browser.ApiAsync(cancellationToken: disconnected.Token);
        await claim.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        disconnected.Cancel();
        claim.Release.TrySetResult();
        await Should.ThrowAsync<OperationCanceledException>(async () => await request);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        using var after = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
        identity.ActiveRefreshTokens.ShouldBe(1);
    }

    [Fact]
    public async Task Logout_RedisDeletedSessionButReplyTimedOut_Returns503WithoutRestoringSession()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gate = new RedisReplyGate(infrastructure);
        using var connection = await gate.ConnectAsync();
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        var deletion = gate.HoldNextSessionReply("GETDEL");

        // Act: exercise the actual internal logout timeout, not a caller cancellation.
        var logout = browser.LogoutAsync(cancellationToken: ct);
        await deletion.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        using var failed = await logout.WaitAsync(TimeSpan.FromSeconds(10), ct);
        deletion.Release.TrySetResult();
        using var after = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.BackendRequests.ShouldBeEmpty();
    }
}
