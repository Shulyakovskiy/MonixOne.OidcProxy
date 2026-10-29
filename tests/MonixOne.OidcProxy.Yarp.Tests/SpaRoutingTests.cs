using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class SpaRoutingTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData("", "/orders/42?tab=history&filter=active", "/orders/42?tab=history&filter=active")]
    [InlineData(
        "/client",
        "~/reports/release.v2?download=1#details",
        "/client/reports/release.v2?download=1#details"
    )]
    [InlineData("", "/#/catalog/items/47", "/#/catalog/items/47")]
    [InlineData(
        "",
        "/%D0%BA%D0%B0%D1%82%D0%B0%D0%BB%D0%BE%D0%B3?next=%2Forders%2F1",
        "/%D0%BA%D0%B0%D1%82%D0%B0%D0%BB%D0%BE%D0%B3?next=%2Forders%2F1"
    )]
    public async Task Login_LocalReturnUrl_PreservesDeepLinkQueryAndFragment(
        string pathBase,
        string returnUrl,
        string expected
    )
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.PathBase = pathBase;
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();

        // Act
        using var login = await browser.LoginAsync(identityBrowser, pathBase, returnUrl);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://attacker.example/path")]
    [InlineData("//attacker.example/path")]
    [InlineData("/\\attacker.example/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/safe\r\nLocation: https://attacker.example")]
    public async Task Login_UnsafeReturnUrl_RejectsBeforeStartingOidc(string returnUrl)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync(
            QueryHelpers.AddQueryString(OidcProxyRoutes.Login, "returnUrl", returnUrl),
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
        identity.CodeRequests.ShouldBe(0);
    }

    [Fact]
    public async Task Login_DuplicateReturnUrl_RejectsAmbiguousNavigation()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync(
            OidcProxyRoutes.Login + "?returnUrl=/one&ReturnUrl=/two",
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task Logout_UnsafeReturnUrl_RejectsWithoutRemovingTheSession()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);

        // Act
        using var logout = await browser.LogoutAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            returnUrl: "//attacker.example"
        );
        using var active = await browser.ApiAsync(
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logout.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        active.StatusCode.ShouldBe(HttpStatusCode.OK);
        identity.RevokedTokens.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/inventory/v9.4/items", "GET")]
    [InlineData("/graphql", "POST")]
    public async Task Proxy_ConfiguredCatchAll_ForwardsArbitraryPathsQueriesAndBodies(
        string path,
        string method
    )
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        var mapping = Request.Create().WithPath(path);
        if (method == "GET")
            mapping.UsingGet();
        else
            mapping.UsingPost();
        identity
            .Server.Given(mapping)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("resource-ok"));
        await using var host = identity.CreateBff();
        host.ProxyPath = "/{**resource}";
        using var browser = host.CreateBrowser();
        using var identityBrowser = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(identityBrowser);
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            path + "?q=one%20two&next=%2Fitem%2F3"
        );
        request.Headers.Add("X-CSRF", "1");
        if (method == "POST")
            request.Content = new StringContent(
                "{\"query\":\"{viewer{id}}\"}",
                Encoding.UTF8,
                "application/json"
            );

        // Act
        using var response = await browser.SendAsync(request, ct);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe("resource-ok");
        var forwarded = identity
            .Server.LogEntries.Single(entry => entry.RequestMessage?.Path == path)
            .RequestMessage!;
        forwarded.Method.ShouldBe(method);
        QueryHelpers.ParseQuery(new Uri(forwarded.Url).Query)["q"].ToString().ShouldBe("one two");
        forwarded.Headers!["Authorization"].Single().ShouldBe("Bearer " + identity.LastAccessToken);
        if (method == "POST")
            forwarded.Body.ShouldBe("{\"query\":\"{viewer{id}}\"}");
    }

    [Theory]
    [InlineData("/customer/jane.doe")]
    [InlineData("/deep/area/section/item?tab=calendar")]
    [InlineData("/report/2026.10/result.json")]
    public async Task Spa_UnknownPagePath_ServesTheSpaShellIncludingDottedSegments(string path)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("monixone-spa-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, "index.html"),
                "<html>spa-shell</html>",
                ct
            );
            using var identity = new OidcWireMock(infrastructure);
            await using var host = identity.CreateBff();
            host.PathBase = "/application";
            host.WebRoot = directory.FullName;
            using var browser = host.CreateBrowser();

            // Act
            using var page = await browser.GetAsync(host.PathBase + path, ct);

            // Assert
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
            (await page.Content.ReadAsStringAsync(ct)).ShouldBe("<html>spa-shell</html>");
            identity.BackendRequests.ShouldBeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
