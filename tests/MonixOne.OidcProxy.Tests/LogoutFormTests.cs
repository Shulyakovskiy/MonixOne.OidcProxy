using System.Net;
using System.Net.Http.Json;

namespace MonixOne.OidcProxy.Tests;

public class LogoutFormTests
{
    [Fact]
    public async Task NativeFormLogoutUsesFrameworkAntiforgeryAndNavigableOidcRedirect()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        var tokenResponse = await browser.SendAsync(HttpMethod.Get, "/.auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
        Assert.DoesNotContain(idp.LastAccessToken, await tokenResponse.Content.ReadAsStringAsync());
        var response = await browser.SendAsync(HttpMethod.Post, "/.auth/end-session", csrf: false,
            origin: BffHost.Origin.GetLeftPart(UriPartial.Authority),
            content: new FormUrlEncodedContent(new Dictionary<string, string>
                { [token["formFieldName"]] = token["requestToken"] }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("identity.example", response.Headers.Location!.Host);
        Assert.Equal(1, idp.Revocations);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, "/.auth/me")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrCrossOriginFormCannotLogUserOut(bool crossOrigin)
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        var tokenResponse = await browser.SendAsync(HttpMethod.Get, "/.auth/antiforgery");
        var token = (await tokenResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
        var response = await browser.SendAsync(HttpMethod.Post, "/.auth/end-session", csrf: false,
            origin: crossOrigin ? "https://attacker.example" : BffHost.Origin.GetLeftPart(UriPartial.Authority),
            content: new FormUrlEncodedContent(new Dictionary<string, string>
                { [token["formFieldName"]] = crossOrigin ? token["requestToken"] : "invalid" }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, idp.Revocations);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, "/.auth/me")).StatusCode);
    }
}
