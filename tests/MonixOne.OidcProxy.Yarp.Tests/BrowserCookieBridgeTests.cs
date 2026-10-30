using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class BrowserCookieBridgeTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Callback_SiblingInjectsAttackerTransaction_DoesNotLinkVictimsCalendar(
        bool explicitPolicy,
        bool script
    )
    {
        // Arrange: the attacker starts a valid transaction in their own browser.
        // The victim has never received a correlation cookie from the admin host.
        await using var fixture = await BrowserCookieBridgeFixture.StartAsync(
            infrastructure,
            explicitPolicy
        );
        await using var attacker = await fixture.NewContextAsync();
        var attackerPage = await attacker.NewPageAsync();
        var startStatus = await fixture.LoginAndStartCalendarAsync(attackerPage);
        var correlation = (await attacker.CookiesAsync()).Single(cookie =>
            cookie.Name.Contains("oidcproxy") && cookie.Name.EndsWith("calendar_google_state")
        );
        await using var victim = await fixture.NewContextAsync();
        var page = await victim.NewPageAsync();
        var attackUrl = QueryHelpers.AddQueryString(
            BrowserCookieBridgeFixture.Sibling + "/attack",
            new Dictionary<string, string?>
            {
                ["name"] = correlation.Name,
                ["script"] = script ? "true" : "false",
            }
        );

        // Act: a hostile sibling tries to plant the attacker's known state, then
        // sends the victim through a genuine cross-site provider navigation.
        await page.GotoAsync(attackUrl);
        var injected = await victim.CookiesAsync([BrowserCookieBridgeFixture.App]);
        await page.GotoAsync(BrowserCookieBridgeFixture.Provider + "/provider");
        var callback = await page.RunAndWaitForResponseAsync(
            () =>
                page.GetByRole(AriaRole.Link, new() { Name = "Complete calendar connection" })
                    .ClickAsync(),
            response => response.Url.StartsWith(fixture.CallbackUrl, StringComparison.Ordinal)
        );

        // Assert
        startStatus.ShouldBe(200);
        callback.Status.ShouldBe(400);
        fixture.Connections.ShouldBe(0);
        injected.ShouldNotContain(cookie => cookie.Name == correlation.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Calendar_CrossSiteCallback_RestoresCookieConsumesOnceAndClearsBrowserState(
        bool explicitPolicy
    )
    {
        // Arrange
        await using var fixture = await BrowserCookieBridgeFixture.StartAsync(
            infrastructure,
            explicitPolicy
        );
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        var startStatus = await fixture.LoginAndStartCalendarAsync(page);
        var correlation = (await context.CookiesAsync()).Single(cookie =>
            cookie.Name.EndsWith("calendar_google_state")
        );

        // Act: a top-level GET from a different site must retain a Lax
        // application correlation cookie, without the Strict BFF session cookie.
        await page.GotoAsync(BrowserCookieBridgeFixture.Provider + "/provider");
        var callback = await page.RunAndWaitForResponseAsync(
            () =>
                page.GetByRole(AriaRole.Link, new() { Name = "Complete calendar connection" })
                    .ClickAsync(),
            response => response.Url.StartsWith(fixture.CallbackUrl, StringComparison.Ordinal)
        );
        var cleared = await context.CookiesAsync();
        var replay = await page.GotoAsync(fixture.CallbackUrl);
        var received = fixture
            .Identity.Server.LogEntries.First(entry =>
                entry.RequestMessage?.Path == BrowserCookieBridgeFixture.Callback
            )
            .RequestMessage!;

        // Assert
        startStatus.ShouldBe(200);
        correlation.Name.ShouldStartWith("__Host-Http-");
        correlation.Domain.ShouldBe("admin.suite.test");
        correlation.Path.ShouldBe("/");
        correlation.Secure.ShouldBeTrue();
        correlation.HttpOnly.ShouldBeTrue();
        correlation.SameSite.ShouldBe(SameSiteAttribute.Lax);
        callback.Status.ShouldBe(200);
        fixture.Connections.ShouldBe(1);
        fixture.Identity.CodeRequests.ShouldBe(1);
        received
            .Headers!["Cookie"]
            .Single()
            .ShouldBe("calendar_google_state=" + BrowserCookieBridgeFixture.State);
        received.Headers.Keys.ShouldNotContain(name =>
            name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        );
        cleared.ShouldNotContain(cookie => cookie.Name == correlation.Name);
        replay!.Status.ShouldBe(400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Calendar_DirectoryOnSameCluster_DoesNotReceiveCallbackCookie(
        bool explicitPolicy
    )
    {
        // Arrange
        await using var fixture = await BrowserCookieBridgeFixture.StartAsync(
            infrastructure,
            explicitPolicy
        );
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        var startStatus = await fixture.LoginAndStartCalendarAsync(page);

        // Act: the browser may send a root-scoped __Host cookie, but the bridge
        // must still enforce the backend's callback path before forwarding it.
        var directoryStatus = await page.EvaluateAsync<int>(
            "async () => (await fetch('/admin/calendar-api/directory?query=', { headers: { 'X-CSRF': '1' } })).status"
        );
        var received = fixture
            .Identity.Server.LogEntries.Single(entry =>
                entry.RequestMessage?.Path == "/calendar-api/directory"
            )
            .RequestMessage!;

        // Assert
        startStatus.ShouldBe(200);
        directoryStatus.ShouldBe(200);
        received.Headers!.Keys.ShouldNotContain(name =>
            name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        );
        received.Headers["Authorization"].Single().ShouldStartWith("Bearer ");
        fixture.Connections.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_LegacyDomainCookie_IsNotAcceptedAfterUpgrade(bool explicitPolicy)
    {
        // Arrange: an existing transaction does not justify accepting the old,
        // injectable cookie format after the package is upgraded.
        await using var fixture = await BrowserCookieBridgeFixture.StartAsync(
            infrastructure,
            explicitPolicy
        );
        await using var attacker = await fixture.NewContextAsync();
        var attackerPage = await attacker.NewPageAsync();
        var startStatus = await fixture.LoginAndStartCalendarAsync(attackerPage);
        var legacyName = explicitPolicy
            ? "__Secure-oidcproxy~spa~calendar~calendar_google_state"
            : "__Secure-oidcproxy-app~spa~YXBp~calendar_google_state";
        await using var victim = await fixture.NewContextAsync();
        var page = await victim.NewPageAsync();

        // Act
        await page.GotoAsync(
            QueryHelpers.AddQueryString(
                BrowserCookieBridgeFixture.Sibling + "/attack",
                "name",
                legacyName
            )
        );
        var legacyCookies = await victim.CookiesAsync([BrowserCookieBridgeFixture.App]);
        var response = await page.GotoAsync(fixture.CallbackUrl);

        // Assert
        startStatus.ShouldBe(200);
        legacyCookies.ShouldContain(cookie => cookie.Name == legacyName);
        response!.Status.ShouldBe(400);
        fixture.Connections.ShouldBe(0);
    }
}
