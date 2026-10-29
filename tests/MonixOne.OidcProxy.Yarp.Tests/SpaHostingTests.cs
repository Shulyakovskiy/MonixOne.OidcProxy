using System.Net;
using Microsoft.AspNetCore.WebUtilities;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class SpaHostingTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData("/admin")]
    [InlineData("/workspace/portal")]
    [InlineData("/client%20area")]
    public async Task LoginApiAndLogout_HostUsesPathBase_KeepCallbacksAndNavigationInsideTheSpa(string pathBase)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.PathBase = pathBase;
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(identityBrowser, pathBase);
        using var api = await browser.ApiAsync(pathBase + "/api/status", ct);
        using var logout = await browser.LogoutAsync(cancellationToken: ct, pathBase: pathBase);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.ShouldBe(pathBase + "/");
        api.StatusCode.ShouldBe(HttpStatusCode.OK);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(logout.Headers.Location!.Query)["post_logout_redirect_uri"].ToString()
            .ShouldBe(new Uri(BffApplicationFactory.Origin, pathBase + OidcProxyRoutes.LogoutCallback).AbsoluteUri);
        identity.ActiveRefreshTokens.ShouldBe(0);
    }
}
