using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MonixOne.OidcProxy.Tests;

public class OidcFlowTests
{
    [Fact]
    public async Task CodePkceLoginIssuesOpaqueSecureCookieAndForwardsOnlyBearer()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        var logged = await browser.LoginAsync(idp);
        Assert.Equal(HttpStatusCode.Redirect, logged.StatusCode);
        Assert.Equal("/home", logged.Headers.Location!.ToString());
        var setCookie = Assert.Single(logged.Headers.GetValues("Set-Cookie"), v => v.StartsWith("__Host-oidcproxy.session="));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        var rawCookie = setCookie.Split(';')[0].Split('=', 2)[1];
        var cookieOptions = host.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BffConstants.CookieScheme);
        var cookieTicket = cookieOptions.TicketDataFormat.Unprotect(rawCookie)!;
        Assert.Empty(cookieTicket.Properties.GetTokens());
        Assert.Single(cookieTicket.Principal.Claims);
        Assert.Equal(BffConstants.SessionClaim, cookieTicket.Principal.Claims.Single().Type);

        var response = await browser.SendAsync(HttpMethod.Get, "/api/data");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string?>>();
        Assert.Equal(idp.LastAccessToken, body!["token"]);
        Assert.Null(body["cookie"]);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var me = await browser.SendAsync(HttpMethod.Get, "/.auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var text = await me.Content.ReadAsStringAsync();
        Assert.Contains("user-1", text);
        Assert.DoesNotContain(idp.LastAccessToken, text);
        Assert.DoesNotContain(idp.LastRefreshToken, text);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("nonce")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("missing_nonce")]
    [InlineData("missing_subject")]
    [InlineData("missing_iat")]
    [InlineData("unsigned")]
    [InlineData("azp")]
    public async Task CallbackRejectsInvalidIdentityTokens(string invalid)
    {
        using var idp = new FakeIdentityProvider { InvalidIdToken = invalid };
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync(idp)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, "/api/data")).StatusCode);
    }

    [Theory]
    [InlineData("missing_access")]
    [InlineData("zero_expiry")]
    [InlineData("wrong_type")]
    public async Task CallbackRejectsMalformedTokenResponses(string failure)
    {
        using var idp = new FakeIdentityProvider { Failure = failure };
        await using var host = await BffHost.StartAsync(idp);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Browser(host).LoginAsync(idp)).StatusCode);
    }

    [Fact]
    public async Task CallbackNeedsProtectedStateAndMatchingBrowserCorrelation()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        var attempt = await browser.BeginLoginAsync(idp);
        var otherBrowser = new Browser(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.SendAsync(HttpMethod.Get, attempt.Callback, csrf: false)).StatusCode);
        Assert.Equal(0, idp.CodeExchanges);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.SendAsync(HttpMethod.Get, "/.auth/login/callback?code=untrusted&state=untrusted", csrf: false)).StatusCode);
        Assert.Equal(0, idp.CodeExchanges);
    }

    [Fact]
    public async Task ReplayedCallbackCannotRedeemCodeTwice()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        var attempt = await browser.BeginLoginAsync(idp);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SendAsync(HttpMethod.Get, attempt.Callback, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, attempt.Callback, csrf: false)).StatusCode);
        Assert.Equal(1, idp.CodeExchanges);
    }

    [Theory]
    [InlineData("/api/data")]
    [InlineData("/.auth/me")]
    [InlineData("/local")]
    public async Task ApiRequiresCsrfEvenForGetAndRejectsOtherOrigins(string path)
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(HttpMethod.Get, path, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await browser.SendAsync(HttpMethod.Get, path, origin: "https://other.app.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await browser.SendAsync(HttpMethod.Get, path, origin: BffHost.Origin.GetLeftPart(UriPartial.Authority))).StatusCode);
    }

    [Fact]
    public async Task LogoutIsPostOnlyProtectedAndInvalidatesServerSession()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        var oldCookies = browser.Cookies;
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await browser.SendAsync(HttpMethod.Get, "/.auth/end-session")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(HttpMethod.Post, "/.auth/end-session", csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SendAsync(HttpMethod.Post, "/.auth/end-session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.SendAsync(HttpMethod.Get, "/api/data", cookies: oldCookies)).StatusCode);
        Assert.Equal(1, idp.Revocations);
    }

    [Fact]
    public async Task ReauthenticationRotatesSessionAndInvalidatesOldCookie()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        var oldCookies = browser.Cookies;
        await browser.LoginAsync(idp);
        Assert.NotEqual(oldCookies, browser.Cookies);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.SendAsync(HttpMethod.Get, "/api/data", cookies: oldCookies)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, "/api/data")).StatusCode);
    }

    [Fact]
    public async Task IndependentHttpRequestsAndMeUseNewTokensAfterRefresh()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        var requests = Enumerable.Range(0, 40).Select(i => browser.SendAsync(HttpMethod.Get, i % 2 == 0 ? "/api/data" : "/.auth/me")).ToArray();
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        idp.RefreshRelease.SetResult();
        var responses = await Task.WhenAll(requests);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, idp.RefreshExchanges);
    }

    [Fact]
    public async Task TwoLoginTabsKeepIndependentPkceAndNonceState()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        var first = await browser.BeginLoginAsync(idp);
        var second = await browser.BeginLoginAsync(idp);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SendAsync(HttpMethod.Get, first.Callback, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SendAsync(HttpMethod.Get, second.Callback, csrf: false)).StatusCode);
        Assert.Equal(2, idp.CodeExchanges);
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, "/api/data")).StatusCode);
        Assert.Equal(1, idp.RefreshExchanges);
    }

    [Fact]
    public async Task RefreshedNonceMustMatchTheOriginalAuthenticatedSession()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        idp.InvalidIdToken = "nonce";
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, "/api/data")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, "/api/data")).StatusCode);
        Assert.Equal(1, idp.RefreshExchanges);
    }
}
