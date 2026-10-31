using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class SessionLifetimeTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData(true, HttpStatusCode.Unauthorized)]
    [InlineData(false, HttpStatusCode.OK)]
    public async Task Session_InspectionCanOptOutOfIdleRenewal(
        bool readOnly,
        HttpStatusCode expected
    )
    {
        // Arrange: polling occurs after token expiry but before session idle expiry.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        using var inspection = await browser.ApiAsync(
            OidcProxyRoutes.User + (readOnly ? "?slide=false" : ""),
            ct
        );
        var refreshesAfterInspection = identity.RefreshRequests;
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        using var later = await browser.ApiAsync(OidcProxyRoutes.User + "?slide=false", ct);

        // Assert: the old endpoint behavior is retained unless explicitly disabled.
        inspection.StatusCode.ShouldBe(HttpStatusCode.OK);
        refreshesAfterInspection.ShouldBe(readOnly ? 0 : 1);
        later.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Session_ExpiresAfterCookieValidation_DoesNotReviveCachedSnapshot()
    {
        // Arrange: expire the record between native validation and BFF activity.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure) { AccessTokenLifetime = 3600 };
        await using var host = identity.CreateBff();
        await using var delayed = host.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.PostConfigure<CookieAuthenticationOptions>(
                    OidcProxyDefaults.CookieScheme("spa"),
                    options =>
                    {
                        var validate = options.Events.OnValidatePrincipal;
                        options.Events.OnValidatePrincipal = async context =>
                        {
                            await validate(context);
                            if (context.Request.Path == "/api/status")
                                host.Clock.Advance(TimeSpan.FromMinutes(21));
                        };
                    }
                )
            )
        );
        using var browser = delayed.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = BffApplicationFactory.Origin,
                AllowAutoRedirect = false,
            }
        );
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);

        // Act
        using var expired = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(0);
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Session_RefreshCrossesPreviousIdleExpiry_KeepsAcceptedActivity()
    {
        // Arrange: refresh starts at +9 while the original idle expiry is +10.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        {
            AccessTokenLifetime = 10,
            ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options =>
            options.SessionIdleTimeout = TimeSpan.FromSeconds(10);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromSeconds(9));

        // Act: the accepted refresh request must cover the old idle boundary.
        var owner = browser.ApiAsync(cancellationToken: ct);
        await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        var waiter = browser.ApiAsync(cancellationToken: ct);
        identity.ReleaseRefresh.TrySetResult();
        using var completed = await owner;
        using var other = await waiter;

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
    }

    [Fact]
    public async Task Session_RecordWithoutRefreshAt_CalculatesThresholdOnActivity()
    {
        // Arrange: RefreshAt is optional; framework protection and session binding remain valid.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        var server = infrastructure.Connection.GetServer(
            infrastructure.Connection.GetEndPoints().Single()
        );
        var key = server
            .Keys(pattern: "test-spa:testing:" + host.KeyNamespace + ":spa:sessions:*")
            .Single();
        var database = infrastructure.Connection.GetDatabase();
        var protector = host.SessionProtector;
        var payload = JsonNode.Parse(
            protector.Unprotect((string)(await database.StringGetAsync(key))!)
        )!;
        payload.AsObject().Remove("RefreshAt");
        var ttl =
            await database.KeyTimeToLiveAsync(key)
            ?? throw new InvalidOperationException("Fixture session must have a TTL.");
        await database.StringSetAsync(key, protector.Protect(payload.ToJsonString()), ttl);

        // Act
        using var initial = await browser.ApiAsync(cancellationToken: ct);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        using var rotated = await browser.ApiAsync(cancellationToken: ct);
        using var repeated = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        initial.StatusCode.ShouldBe(HttpStatusCode.OK);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
    }

    [Fact]
    public async Task Session_RequestBeforeHalfIdleInterval_ExtendsFromThatActivity()
    {
        // Arrange: a long-lived access token keeps this test independent of refresh.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure) { AccessTokenLifetime = 3600 };
        await using var host = identity.CreateBff();
        using var active = host.CreateBrowser();
        using var idle = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var activeLogin = await active.LoginAsync(provider);
        using var idleLogin = await idle.LoginAsync(provider);

        // Act: both sessions are active at +9; only the first is active again at +21.
        host.Clock.Advance(TimeSpan.FromMinutes(9));
        using var firstActivity = await active.ApiAsync(cancellationToken: ct);
        using var otherActivity = await idle.ApiAsync(cancellationToken: ct);
        host.Clock.Advance(TimeSpan.FromMinutes(12));
        using var activeAt21 = await active.ApiAsync(cancellationToken: ct);
        host.Clock.Advance(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(1));
        using var idleAfter29 = await idle.ApiAsync(cancellationToken: ct);

        // Assert
        activeLogin.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        idleLogin.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        firstActivity.StatusCode.ShouldBe(HttpStatusCode.OK);
        otherActivity.StatusCode.ShouldBe(HttpStatusCode.OK);
        activeAt21.StatusCode.ShouldBe(HttpStatusCode.OK);
        idleAfter29.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Session_ValidShortAccessTokenWithoutRefresh_TouchesUntilTokenExpiration()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure)
        {
            AccessTokenLifetime = 25,
            IssueRefreshToken = false,
        };
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options =>
            options.SessionIdleTimeout = TimeSpan.FromSeconds(10);
        host.ConfigureOidc = options => options.Scope.Remove("offline_access");
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);

        // Act
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        using var at6 = await browser.ApiAsync(cancellationToken: ct);
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        using var at12 = await browser.ApiAsync(cancellationToken: ct);
        host.Clock.Advance(TimeSpan.FromSeconds(8));
        using var at20 = await browser.ApiAsync(cancellationToken: ct);
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        using var expired = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        at6.StatusCode.ShouldBe(HttpStatusCode.OK);
        at12.StatusCode.ShouldBe(HttpStatusCode.OK);
        at20.StatusCode.ShouldBe(HttpStatusCode.OK);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(120)]
    public async Task Refresh_ShortLivedAccessToken_RotatesOnlyAtRelativeThreshold(int lifetime)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure) { AccessTokenLifetime = lifetime };
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);

        // Act
        using var immediate = await browser.ApiAsync(cancellationToken: ct);
        using var immediateAgain = await browser.ApiAsync(cancellationToken: ct);
        var initialRedemptions = identity.RefreshRequests;
        host.Clock.Advance(TimeSpan.FromTicks(lifetime * TimeSpan.TicksPerSecond * 9 / 10));
        using var threshold = await browser.ApiAsync(cancellationToken: ct);
        using var afterRotation = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        immediate.StatusCode.ShouldBe(HttpStatusCode.OK);
        immediateAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
        initialRedemptions.ShouldBe(0);
        threshold.StatusCode.ShouldBe(HttpStatusCode.OK);
        afterRotation.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RefreshRequests.ShouldBe(1);
    }

    [Fact]
    public async Task Session_ContinuousActivity_DoesNotExtendAbsoluteExpiration()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure) { AccessTokenLifetime = 3600 };
        await using var host = identity.CreateBff();
        host.ConfigureClientOptions = options =>
        {
            options.SessionIdleTimeout = TimeSpan.FromSeconds(10);
            options.SessionAbsoluteTimeout = TimeSpan.FromSeconds(20);
        };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            host.Clock.Advance(TimeSpan.FromSeconds(6));
            using var response = await browser.ApiAsync(cancellationToken: ct);
            statuses.Add(response.StatusCode);
        }
        host.Clock.Advance(TimeSpan.FromSeconds(3));
        using var expired = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        statuses.ShouldAllBe(status => status == HttpStatusCode.OK);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        identity.RefreshRequests.ShouldBe(0);
    }
}
