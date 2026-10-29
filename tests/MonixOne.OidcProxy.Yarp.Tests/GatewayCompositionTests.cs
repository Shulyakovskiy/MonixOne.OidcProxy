using System.Net;
using Microsoft.AspNetCore.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Yarp.ReverseProxy.Configuration;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class GatewayCompositionTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Proxy_UnmanagedHttpCluster_KeepsNativeGatewayForwarding()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        using var native = WireMockServer.Start();
        native
            .Given(Request.Create().WithPath("/legacy/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("native-upstream"));
        await using var host = identity.CreateBff();
        host.ConfigureClusters = clusters =>
            clusters.Add(
                new()
                {
                    ClusterId = "native",
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["primary"] = new() { Address = native.Urls[0] },
                    },
                }
            );
        host.ConfigureRoutes = routes =>
            routes.Add(
                new()
                {
                    RouteId = "native",
                    ClusterId = "native",
                    Match = new() { Hosts = ["native.example"], Path = "/legacy/{**path}" },
                }
            );
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync("https://native.example/legacy/status", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe("native-upstream");
    }

    [Fact]
    public async Task Proxy_TlsTerminationWithHttpCluster_ForwardsServerTokenAndPublicScheme()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure);
        using var backend = WireMockServer.Start();
        backend
            .Given(Request.Create().WithPath("/api/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("internal-upstream"));
        await using var host = identity.CreateBff();
        host.ConfigurationValues["ForwardedHeaders:KnownProxies:0"] = "10.20.30.40";
        host.ConfigurationValues["ForwardedHeaders:AllowedHosts:0"] = BffApplicationFactory
            .Origin
            .Host;
        host.ConfigurationValues["https_port"] = "443";
        host.ConfigureClusters = clusters =>
            clusters[0] = clusters[0] with
            {
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["primary"] = new() { Address = backend.Urls[0] },
                },
            };
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);

        // Act
        var response = await host.Server.SendAsync(
            context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.30.40");
                context.Request.Method = "GET";
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("internal-bff", 8000);
                context.Request.Path = "/api/status";
                context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                context.Request.Headers["X-Forwarded-Host"] = BffApplicationFactory.Origin.Host;
                context.Request.Headers["X-CSRF"] = "1";
                context.Request.Headers.Cookie = login
                    .Headers.GetValues("Set-Cookie")
                    .Single(value =>
                        value.StartsWith(
                            OidcProxyDefaults.CookieName("spa") + "=",
                            StringComparison.Ordinal
                        )
                    )
                    .Split(';')[0];
            },
            ct
        );

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        using var body = new StreamReader(response.Response.Body);
        (await body.ReadToEndAsync(ct)).ShouldBe("internal-upstream");
        var forwarded = backend.LogEntries.Single().RequestMessage!;
        forwarded.Headers!["Authorization"].Single().ShouldBe("Bearer " + identity.LastAccessToken);
        forwarded.Headers["X-Forwarded-Proto"].Single().ShouldBe("https");
        forwarded.Headers["X-Forwarded-Host"].Single().ShouldBe(BffApplicationFactory.Origin.Host);
        forwarded.Headers.Keys.ShouldNotContain(key =>
            key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("http://user:password@127.0.0.1/")]
    [InlineData("http://127.0.0.1/?query=value")]
    [InlineData("http://127.0.0.1/#fragment")]
    public async Task Startup_InvalidDestination_RejectsBeforeRequests(string address)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureClusters = clusters =>
            clusters[0] = clusters[0] with
            {
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["primary"] = new() { Address = address },
                },
            };

        // Act
        var error = Should.Throw<InvalidOperationException>(() => host.CreateBrowser());

        // Assert
        error.Message.ShouldContain("configuration");
        identity.BackendRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Startup_CertificateValidationDisabled_RejectsBeforeRequests()
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureClusters = clusters =>
            clusters[0] = clusters[0] with
            {
                HttpClient = new() { DangerousAcceptAnyServerCertificate = true },
            };

        // Act
        var error = Should.Throw<InvalidOperationException>(() => host.CreateBrowser());

        // Assert
        error.Message.ShouldContain("configuration");
        identity.BackendRequests.ShouldBeEmpty();
    }
}
