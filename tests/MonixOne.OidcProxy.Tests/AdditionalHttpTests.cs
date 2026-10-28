using System.Net;
using System.Net.Http.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MonixOne.OidcProxy.Tests;

public class AdditionalHttpTests
{
    [Theory]
    [InlineData("X-CSRF", "wrong")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Origin", "null")]
    public async Task ForgedOrDuplicateCsrfAndCrossOriginMetadataAreRejected(string name, string value)
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        var response = await browser.SendAsync(HttpMethod.Get, "/api/data", headers: new Dictionary<string, string> { [name] = value });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task IncomingBearerCannotReplaceTheServerSideUserToken()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var browser = new Browser(host);
        var header = new Dictionary<string, string> { ["Authorization"] = "Bearer attacker-token" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Get, "/api/data", headers: header)).StatusCode);
        await browser.LoginAsync(idp);
        var response = await browser.SendAsync(HttpMethod.Get, "/api/data", headers: header);
        var data = await response.Content.ReadFromJsonAsync<Dictionary<string, string?>>();
        Assert.Equal(idp.LastAccessToken, data!["token"]);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task RefreshedIdTokenHandlesSigningKeyRolloverUsingFrameworkConfigurationManager()
    {
        using var idp = new FakeIdentityProvider();
        var manager = new RotatingConfigurationManager(idp.Configuration(), idp.Configuration);
        await using var host = await BffHost.StartAsync(idp, configurationManager: manager);
        var browser = new Browser(host);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync(idp)).StatusCode);
        idp.RotateSigningKey();
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, "/api/data")).StatusCode);
        Assert.True(manager.Refreshes > 0);
        Assert.Equal(1, idp.RefreshExchanges);
    }

    [Theory]
    [InlineData("/api/data")]
    [InlineData("/.auth/me")]
    [InlineData("/local")]
    public async Task PrincipalFromAnotherHostAuthenticationSchemeCannotBypassBffSession(string path)
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp, injectForeignPrincipal: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Browser(host).SendAsync(HttpMethod.Get, path)).StatusCode);
    }

    private sealed class RotatingConfigurationManager(OpenIdConnectConfiguration original, Func<OpenIdConnectConfiguration> updated)
        : BaseConfigurationManager, IConfigurationManager<OpenIdConnectConfiguration>
    {
        internal int Refreshes;
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
            => Task.FromResult(Volatile.Read(ref Refreshes) == 0 ? original : updated());
        public override async Task<BaseConfiguration> GetBaseConfigurationAsync(CancellationToken cancel)
            => await GetConfigurationAsync(cancel);
        public override void RequestRefresh() => Interlocked.Increment(ref Refreshes);
    }
}
