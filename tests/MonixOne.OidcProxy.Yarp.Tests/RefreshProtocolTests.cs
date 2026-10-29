using System.Net;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class RefreshProtocolTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("nonce")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    [InlineData("subject")]
    public async Task Refresh_InvalidIdentityToken_InvalidatesSessionAndRevokesIssuedCredentials(string defect)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        identity.InvalidIdToken = defect;
        host.Clock.Advance(TimeSpan.FromSeconds(110));

        // Act
        using var refresh = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var after = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(1);
        identity.ActiveRefreshTokens.ShouldBe(0);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_OptionalTokensOmitted_PreservesExistingCredentials(bool omitRefreshToken)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure)
        { OmitRefreshIdentityToken = true, OmitRotatedRefreshToken = omitRefreshToken };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);

        // Act
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        using var first = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        using var second = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var logout = await browser.LogoutAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        identity.RefreshRequests.ShouldBe(2);
        identity.ActiveRefreshTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_ProviderSigningKeyRollover_LoadsTheNewJwksKey()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        identity.RotateSigningKey();
        host.Clock.Advance(TimeSpan.FromSeconds(110));

        // Act
        using var response = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
        identity.ActiveRefreshTokens.ShouldBe(1);
    }

    [Fact]
    public async Task Refresh_ExpiredPendingOperation_DoesNotRetryOrRestoreTheSession()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        host.Clock.Advance(TimeSpan.FromSeconds(110));

        // Act
        var owner = browser.ApiAsync(cancellationToken: ct);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        using var waiter = await browser.ApiAsync(cancellationToken: ct);
        identity.ReleaseRefresh.SetResult();
        using var late = await owner;

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        waiter.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        late.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(1);
        identity.ActiveRefreshTokens.ShouldBe(0);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoginAndRefresh_SignedEncryptedIdentityTokens_UseFrameworkDecryption()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure) { EncryptIdentityTokens = true };
        await using var host = identity.CreateBff();
        host.ConfigureOidc = options => options.TokenValidationParameters.TokenDecryptionKey = identity.EncryptionKey;
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(identityBrowser);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        using var refresh = await browser.ApiAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
    }
}
