using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class BackchannelLogoutTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Startup_ReplayRetentionShorterThanAcceptedTokenAge_RejectsUnsafePolicy()
    {
        // Arrange: such a policy would let a valid replay close a later login after dedup expires.
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options =>
            options.BackchannelLogout.ReplayRetention = options.BackchannelLogout.MaximumTokenAge;

        // Act
        var failure = Should.Throw<OptionsValidationException>(() => host.CreateBrowser());

        // Assert
        failure.Message.ShouldContain("ReplayRetention");
        identity.CodeRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Logout_OneClient_DoesNotCloseAnotherClientsSession()
    {
        // Arrange: independent OIDC clients share one gateway and Redis connection.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        using var secondIdentity = new OidcWireMock(infrastructure, "second-client");
        await using var host = identity.CreateBff();
        var secondOrigin = new Uri("https://second.example");
        host.AddClient("second", secondOrigin, secondIdentity);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var secondProvider = secondIdentity.CreateIdentityBrowser();
        using var firstLogin = await browser.LoginAsync(provider);
        using var secondLogin = await browser.LoginAsync(secondProvider, origin: secondOrigin);

        // Act
        using var logout = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["logout_token"] = identity.CreateLogoutToken() }
            ),
            ct
        );
        using var firstRejected = await browser.ApiAsync(cancellationToken: ct);
        using var secondAccepted = await browser.ApiAsync(
            new Uri(secondOrigin, "/api/status").AbsoluteUri,
            ct
        );

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstRejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        secondAccepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_OptionalExpiryOrSigningKeyRollover_UsesProviderProtocolMetadata(
        bool rollover
    )
    {
        // Arrange: the key ring was discovered during login before the IdP changes its signing key.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        if (rollover)
            identity.RotateSigningKey();
        var token = identity.CreateLogoutToken(defect: rollover ? null : "no-exp");

        // Act
        using var logout = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }),
            ct
        );
        var finalStatus = logout.StatusCode;
        if (rollover && finalStatus == HttpStatusCode.ServiceUnavailable)
        {
            // Model provider redelivery after the framework's asynchronous metadata refresh.
            // Poll observable cached keys rather than assuming a fixed network completion time.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var oidc = host
                .Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(OidcProxyDefaults.OpenIdConnectScheme("spa"));
            while (
                !(
                    await oidc.ConfigurationManager!.GetConfigurationAsync(timeout.Token)
                ).SigningKeys.Any(key => key.KeyId == "rotated-key")
            )
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            using var retry = await browser.PostAsync(
                OidcProxyRoutes.BackchannelLogout,
                new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["logout_token"] = token }
                ),
                ct
            );
            finalStatus = retry.StatusCode;
        }
        using var rejected = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        finalStatus.ShouldBe(HttpStatusCode.OK);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_RedisReplyLost_RetryConfirmsTheCommittedLogout()
    {
        // Arrange: hold the reply after Redis has deleted sessions and written the replay marker.
        var ct = TestContext.Current.CancellationToken;
        await using var gate = new RedisReplyGate(infrastructure);
        using var connection = await gate.ConnectAsync(asyncTimeout: 1000);
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff(connection: connection);
        await using var replica = identity.CreateBff(host.KeyNamespace);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        using var sender = replica.CreateBrowser(handleCookies: false);
        var token = identity.CreateLogoutToken();
        var commit = gate.HoldNextSessionReply("EVAL");
        try
        {
            // Act
            var pending = browser.PostAsync(
                OidcProxyRoutes.BackchannelLogout,
                new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["logout_token"] = token }
                ),
                ct
            );
            await commit.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            using var uncertain = await pending;
            commit.Release.TrySetResult();
            using var retry = await sender.PostAsync(
                OidcProxyRoutes.BackchannelLogout,
                new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["logout_token"] = token }
                ),
                ct
            );
            using var rejected = await browser.ApiAsync(cancellationToken: ct);

            // Assert
            uncertain.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            retry.StatusCode.ShouldBe(HttpStatusCode.OK);
            rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            commit.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Logout_BatchBudgetExceeded_RetryCompletesWithoutPrematureReplayMarker()
    {
        // Arrange: two sessions require two deliveries under the configured single-record budget.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options =>
        {
            options.BackchannelLogout.BatchSize = 1;
            options.BackchannelLogout.MaximumBatches = 1;
        };
        using var first = host.CreateBrowser();
        using var second = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await first.LoginAsync(provider);
        using var anotherLogin = await second.LoginAsync(provider);
        var token = identity.CreateLogoutToken();

        // Act
        using var partial = await first.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }),
            ct
        );
        using var retry = await first.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }),
            ct
        );
        using var firstRejected = await first.ApiAsync(cancellationToken: ct);
        using var secondRejected = await second.ApiAsync(cancellationToken: ct);

        // Assert
        partial.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstRejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        secondRejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_ConfiguredTokenLimit_RejectsBeforeSessionMutation()
    {
        // Arrange: override the operational limit while retaining the existing endpoint contract.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        var token = identity.CreateLogoutToken();
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options =>
            options.BackchannelLogout.MaximumTokenLength = token.Length - 1;
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);

        // Act
        using var rejected = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }),
            ct
        );
        using var accepted = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("user-1", "provider-session")]
    [InlineData("user-1", null)]
    [InlineData(null, "provider-session")]
    [InlineData("user-1", "provider-session", "/portal")]
    public async Task Logout_ValidNotification_ClosesMatchingSessionsAcrossReplicas(
        string? subject,
        string? sid,
        string pathBase = ""
    )
    {
        // Arrange: each replica authenticates the same cookie against shared Redis.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        await using var replica = identity.CreateBff(host.KeyNamespace);
        host.PathBase = pathBase;
        replica.PathBase = pathBase;
        using var browser = host.CreateBrowser();
        using var other = replica.CreateBrowser(handleCookies: false);
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider, pathBase);
        var cookie = login
            .Headers.GetValues("Set-Cookie")
            .Single(v =>
                v.StartsWith(OidcProxyDefaults.CookieName("spa") + "=", StringComparison.Ordinal)
            )
            .Split(';')[0];
        other.DefaultRequestHeaders.Add("Cookie", cookie);
        using var sender = host.CreateBrowser(handleCookies: false);

        // Act: provider notifications need no browser cookie or CSRF header.
        using var logout = await sender.PostAsync(
            pathBase + OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["logout_token"] = identity.CreateLogoutToken(subject, sid),
                }
            ),
            ct
        );
        using var rejected = await other.ApiAsync(pathBase + "/api/status", ct);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);
        logout.Headers.CacheControl!.NoStore.ShouldBeTrue();
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("nonce")]
    [InlineData("events")]
    [InlineData("missing-jti")]
    [InlineData("unsigned")]
    [InlineData("issuer", true)]
    [InlineData("audience", true)]
    public async Task Logout_InvalidToken_DoesNotEndTheSession(
        string defect,
        bool permissiveInteractiveValidators = false
    )
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        if (permissiveInteractiveValidators)
        {
            // A host may customize interactive ID-token validation; provider logout
            // must still be confined to its exact issuer and audience.
            var parameters = host
                .Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(OidcProxyDefaults.OpenIdConnectScheme("spa"))
                .TokenValidationParameters;
            parameters.IssuerValidator = (issuer, _, _) => issuer;
            parameters.AudienceValidator = (_, _, _) => true;
        }

        // Act
        using var logout = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["logout_token"] = identity.CreateLogoutToken(defect: defect),
                }
            ),
            ct
        );
        using var accepted = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("other-user", "provider-session")]
    [InlineData("user-1", "other-session")]
    public async Task Logout_NonmatchingIdentity_DoesNotEndAnotherSession(
        string subject,
        string sid
    )
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);

        // Act
        using var logout = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["logout_token"] = identity.CreateLogoutToken(subject, sid),
                }
            ),
            ct
        );
        using var accepted = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_DuplicateAfterNewLogin_DoesNotRevokeTheNewSession()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var first = await browser.LoginAsync(provider);
        var token = identity.CreateLogoutToken();
        using var initial = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }),
            ct
        );
        using var second = await browser.LoginAsync(provider);

        // Act
        using var duplicate = await browser.PostAsync(
            OidcProxyRoutes.BackchannelLogout,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }),
            ct
        );
        using var accepted = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        initial.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_RefreshInFlight_DoesNotRestoreTheRevokedSession()
    {
        // Arrange: hold the provider response after its rotating grant was redeemed.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        {
            ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        var pending = browser.ApiAsync(cancellationToken: ct);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        try
        {
            // Act
            using var logout = await browser.PostAsync(
                OidcProxyRoutes.BackchannelLogout,
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["logout_token"] = identity.CreateLogoutToken(),
                    }
                ),
                ct
            );
            identity.ReleaseRefresh.TrySetResult();
            using var rejected = await pending;

            // Assert
            logout.StatusCode.ShouldBe(HttpStatusCode.OK);
            rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            identity.ActiveRefreshTokens.ShouldBe(0);
            identity.BackendRequests.ShouldBeEmpty();
        }
        finally
        {
            identity.ReleaseRefresh.TrySetResult();
        }
    }
}
