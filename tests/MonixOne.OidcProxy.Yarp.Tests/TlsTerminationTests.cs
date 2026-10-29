using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class TlsTerminationTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData("KnownProxies", "10.20.30.40")]
    [InlineData("KnownIPNetworks", "10.20.30.0/24")]
    public async Task Login_TrustedTlsTerminatingProxy_RestoresHttpsHostAndMountedCallback(
        string setting,
        string proxy
    )
    {
        // Arrange: the real sample pipeline receives HTTP from a configured proxy.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.PathBase = "/admin";
        host.ConfigurationValues[$"ForwardedHeaders:{setting}:0"] = proxy;
        host.ConfigurationValues["ForwardedHeaders:AllowedHosts:0"] = BffApplicationFactory
            .Origin
            .Host;
        host.ConfigurationValues["https_port"] = "443";

        // Act
        var response = await host.Server.SendAsync(
            context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.30.40");
                context.Request.Method = "GET";
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("internal-bff", 8000);
                context.Request.Path = "/admin/.auth/login";
                context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                context.Request.Headers["X-Forwarded-Host"] = BffApplicationFactory.Origin.Host;
            },
            ct
        );

        // Assert
        response.Response.StatusCode.ShouldBe(StatusCodes.Status302Found);
        var location = new Uri(response.Response.Headers.Location.ToString());
        location
            .GetLeftPart(UriPartial.Authority)
            .ShouldBe(identity.Origin.GetLeftPart(UriPartial.Authority));
        QueryHelpers
            .ParseQuery(location.Query)["redirect_uri"]
            .ToString()
            .ShouldBe("https://spa.example/admin/.auth/login/callback");
        response.Response.Headers.SetCookie.ShouldNotBeEmpty();
        foreach (var cookie in response.Response.Headers.SetCookie)
            cookie!.ShouldContain("secure", Case.Insensitive);
    }

    [Fact]
    public async Task Login_UntrustedForwardedHttps_DoesNotStartOidcOrIssueCookies()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigurationValues["ForwardedHeaders:KnownProxies:0"] = "10.20.30.40";
        host.ConfigurationValues["https_port"] = "443";

        // Act: another source cannot impersonate the TLS-terminating proxy.
        var response = await host.Server.SendAsync(
            context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.30.41");
                context.Request.Method = "GET";
                context.Request.Scheme = "http";
                context.Request.Host = new HostString(BffApplicationFactory.Origin.Host);
                context.Request.Path = "/.auth/login";
                context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                context.Request.Headers["X-Forwarded-Host"] = BffApplicationFactory.Origin.Host;
            },
            ct
        );

        // Assert
        response.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        response.Response.Headers.Location.ShouldBeEmpty();
        response.Response.Headers.SetCookie.ShouldBeEmpty();
    }
}
