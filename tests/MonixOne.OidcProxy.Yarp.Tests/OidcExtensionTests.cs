using System.Net;
using System.Security.Claims;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class OidcExtensionTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData(ClientCredentialStyle.PostBody, false)]
    [InlineData(ClientCredentialStyle.AuthorizationHeader, true)]
    public async Task LoginRefreshLogout_IncludingPar_UsesOneConfiguredClientAuthentication(
        ClientCredentialStyle style,
        bool requireBasic
    )
    {
        // Arrange: the provider accepts the selected authentication style for PAR and every grant.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        {
            RequireBasicClientAuthentication = requireBasic,
            AdvertisePushedAuthorization = true,
        };
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options => options.ClientCredentialStyle = style;
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        using var accepted = await browser.ApiAsync(cancellationToken: ct);
        using var logout = await browser.LogoutAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        identity.CodeRequests.ShouldBe(1);
        identity.PushedRequests.ShouldBe(1);
        identity.RefreshRequests.ShouldBe(1);
        identity.ActiveRefreshTokens.ShouldBe(0);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("unsigned")]
    public async Task Login_BasicClientAuthentication_RetainsNativeTokenValidation(string defect)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure)
        {
            RequireBasicClientAuthentication = true,
            InvalidIdToken = defect,
        };
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options =>
            options.ClientCredentialStyle = ClientCredentialStyle.AuthorizationHeader;
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.ActiveRefreshTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Login_HostTicketCallback_PersistsItsClaimsInTheServerSession()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        var calls = 0;
        host.ConfigureOidc = options =>
            options.Events.OnTicketReceived = context =>
            {
                Interlocked.Increment(ref calls);
                ((ClaimsIdentity)context.Principal!.Identity!).AddClaim(
                    new Claim("host-claim", "accepted")
                );
                return Task.CompletedTask;
            };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider);
        using var session = await browser.ApiAsync(OidcProxyRoutes.User, ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        calls.ShouldBe(1);
        (await session.Content.ReadAsStringAsync(ct)).ShouldContain("host-claim");
    }

    [Fact]
    public async Task Login_HostReplacesProperties_KeepsReferenceCookieAndProviderLogout()
    {
        // Arrange: the host replaces properties after native token validation.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureOidc = options =>
            options.Events.OnTicketReceived = context =>
            {
                context.Properties = new AuthenticationProperties();
                return Task.CompletedTask;
            };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider, rememberMe: true);
        var cookie = login
            .Headers.GetValues("Set-Cookie")
            .Select(value => SetCookieHeaderValue.Parse(value))
            .Single(value => value.Name.ToString() == OidcProxyDefaults.CookieName("spa"));
        var ticket = host
            .Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(OidcProxyDefaults.CookieScheme("spa"))
            .TicketDataFormat.Unprotect(cookie.Value.ToString());
        using var active = await browser.ApiAsync(cancellationToken: ct);
        using var logout = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["logout_token"] = identity.CreateLogoutToken() }
            ),
            ct
        );
        using var denied = await browser.ApiAsync(cancellationToken: ct);

        // Assert: credentials stay server-side and mandatory provider indices survive.
        ticket.ShouldNotBeNull();
        ticket.Properties.GetTokens().ShouldBeEmpty();
        ticket.Principal.Claims.ShouldHaveSingleItem();
        ticket.Properties.IsPersistent.ShouldBeTrue();
        cookie.Expires.ShouldNotBeNull();
        active.StatusCode.ShouldBe(HttpStatusCode.OK);
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("validated")]
    [InlineData("response")]
    [InlineData("failed")]
    public async Task Login_HostHandlesEvent_ResponseDoesNotLeaveIssuedCredentials(string stage)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        if (stage == "failed")
            identity.InvalidIdToken = "nonce";
        await using var host = identity.CreateBff();
        host.ConfigureOidc = options =>
        {
            if (stage == "ticket")
                options.Events.OnTicketReceived = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            else if (stage == "validated")
                options.Events.OnTokenValidated = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            else if (stage == "response")
                options.Events.OnTokenResponseReceived = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            else
                options.Events.OnAuthenticationFailed = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
        };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(provider);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        identity.ActiveRefreshTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Startup_TypedEvents_RejectsBypassOfBffSessionHandling()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureOidc = options => options.EventsType = typeof(OpenIdConnectEvents);

        // Act
        var failure = Should.Throw<OptionsValidationException>(() => host.CreateBrowser());

        // Assert
        failure.Message.ShouldContain("EventsType");
        identity.CodeRequests.ShouldBe(0);
    }
}
