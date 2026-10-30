using System.Net;
using System.Text.Json.Nodes;
using Jose;
using Microsoft.Net.Http.Headers;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class RememberedSessionTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_ProtectedChoiceControlsCookieAndRedisLifetime(bool? rememberMe)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        // Act
        using var login = await browser.LoginAsync(provider, rememberMe: rememberMe);

        // Assert: the browser cookie and the encrypted server record share one policy.
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var cookie = login
            .Headers.GetValues("Set-Cookie")
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.ToString() == OidcProxyDefaults.CookieName("spa"));
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        var server = infrastructure.Connection.GetServer(
            infrastructure.Connection.GetEndPoints().Single()
        );
        var key = server
            .Keys(pattern: "test-spa:testing:" + host.KeyNamespace + ":spa:sessions:*")
            .Single();
        var database = infrastructure.Connection.GetDatabase();
        var state = JsonNode.Parse(
            JWT.Decrypt(
                (string)(await database.StringGetAsync(key))!,
                infrastructure.EncryptionKey,
                JweAlgorithm.PBES2_HS256_A128KW,
                JweEncryption.A256GCM
            )
        )!;
        var absolute = state["AbsoluteExpiresAt"]!.GetValue<DateTimeOffset>();
        var idle = state["IdleExpiresAt"]!.GetValue<DateTimeOffset>();
        var ttl = await database.KeyTimeToLiveAsync(key);
        if (rememberMe == true)
        {
            (cookie.Expires!.Value - absolute).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
            absolute.ShouldBe(host.Clock.GetUtcNow() + TimeSpan.FromDays(30));
            idle.ShouldBe(absolute);
            ttl!.Value.ShouldBeGreaterThan(TimeSpan.FromDays(29));
        }
        else
        {
            cookie.Expires.ShouldBeNull();
            cookie.MaxAge.ShouldBeNull();
            absolute.ShouldBe(host.Clock.GetUtcNow() + TimeSpan.FromHours(8));
            idle.ShouldBe(host.Clock.GetUtcNow() + TimeSpan.FromMinutes(20));
            ttl!.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(20));
        }
    }

    [Fact]
    public async Task RememberedSession_RemainsActiveAfterInactivity_ButExpiresAtThirtyDays()
    {
        // Arrange: a long-lived access token isolates session expiry from refresh.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        {
            AccessTokenLifetime = 31 * 24 * 3600,
        };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, rememberMe: true);
        host.Clock.Advance(TimeSpan.FromDays(29));

        // Act
        using var active = await browser.ApiAsync(cancellationToken: ct);
        host.Clock.Advance(TimeSpan.FromDays(1));
        using var expired = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        active.StatusCode.ShouldBe(HttpStatusCode.OK);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData("rememberMe=1")]
    [InlineData("rememberMe=")]
    [InlineData("rememberMe=true&rememberMe=false")]
    public async Task Login_InvalidRememberedChoice_DoesNotStartOidc(string query)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        // Act
        using var result = await browser.GetAsync(
            OidcProxyRoutes.Login + "?" + query,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task RememberedSession_ExplicitLogoutRemovesTheServerSession()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, rememberMe: true);
        // Act
        using var logout = await browser.LogoutAsync(
            cancellationToken: TestContext.Current.CancellationToken
        );
        using var denied = await browser.ApiAsync(
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
