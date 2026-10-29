using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class OpenIddictFlowTests(InfrastructureFixture infrastructure, OpenIddictHost identity)
    : IClassFixture<OpenIddictHost>
{
    [Fact]
    public async Task Proxy_OpenIddictEncryptedAccessToken_ReachesAnAuthenticatedResourceApi()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clientId = await identity.RegisterClientAsync();
        await using var host = new BffApplicationFactory(infrastructure, identity.Origin, clientId, OpenIddictHost.Secret, identity.Origin);
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateBrowser();
        using var identitySession = await identityBrowser.PostAsync("/test/session", null, ct);

        // Act
        using var login = await browser.LoginAsync(identityBrowser);
        using var response = await browser.ApiAsync(cancellationToken: ct);
        var resource = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        using var me = await browser.ApiAsync("/.auth/me", ct);

        // Assert
        identitySession.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        resource.GetProperty("subject").GetString().ShouldBe(OpenIddictHost.Subject);
        resource.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ShouldContain("reader");
        resource.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()).ShouldContain("api.read");
        identity.ResourceTokens.Last().Split('.').Length.ShouldBe(5); // Real JWE; the BFF must not decode access-token claims.
        var claims = await me.Content.ReadFromJsonAsync<Dictionary<string, string[]>>(ct);
        claims!.ShouldNotContainKey("role");
    }

    [Fact]
    public async Task Refresh_RealOpenIddictWithConcurrentRequests_RotatesOnceAndPreservesResourceAccess()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clientId = await identity.RegisterClientAsync();
        await using var host = new BffApplicationFactory(infrastructure, identity.Origin, clientId, OpenIddictHost.Secret, identity.Origin);
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateBrowser();
        using var session = await identityBrowser.PostAsync("/test/session", null, ct);
        using var login = await browser.LoginAsync(identityBrowser);
        host.Clock.Advance(TimeSpan.FromSeconds(100));

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => browser.ApiAsync(cancellationToken: ct)));

        // Assert
        session.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        identity.RefreshRequests[clientId].ShouldBe(1);
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_RealOpenIddict_CompletesRpLogoutAndEndsTheBffSession(bool nativeForm)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clientId = await identity.RegisterClientAsync();
        await using var host = new BffApplicationFactory(infrastructure, identity.Origin, clientId, OpenIddictHost.Secret, identity.Origin);
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateBrowser();
        using var session = await identityBrowser.PostAsync("/test/session", null, ct);
        using var login = await browser.LoginAsync(identityBrowser);

        // Act
        using var logout = await browser.LogoutAsync(nativeForm, ct);
        using var remote = await identityBrowser.GetAsync(logout.Headers.Location, ct);
        using var callback = await browser.GetAsync(remote.Headers.Location!.PathAndQuery, ct);
        using var after = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        session.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        remote.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.ShouldBe("/");
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_PublicOpenIddictClient_CannotAuthenticateAsAConfidentialBff()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clientId = await identity.RegisterClientAsync(publicClient: true);
        await using var host = new BffApplicationFactory(infrastructure, identity.Origin, clientId, OpenIddictHost.Secret, identity.Origin);
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateBrowser();
        using var session = await identityBrowser.PostAsync("/test/session", null, ct);

        // Act
        using var login = await browser.LoginAsync(identityBrowser);
        using var api = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        session.StatusCode.ShouldBe(HttpStatusCode.OK);
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_OpenIddictWithoutUserSession_DoesNotIssueAnonymousCredentials()
    {
        // Arrange
        var clientId = await identity.RegisterClientAsync();
        await using var host = new BffApplicationFactory(infrastructure, identity.Origin, clientId, OpenIddictHost.Secret, identity.Origin);
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateBrowser();

        // Act
        using var login = await browser.LoginAsync(identityBrowser);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
