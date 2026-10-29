using System.Net;
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
        native.Given(Request.Create().WithPath("/legacy/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("native-upstream"));
        await using var host = identity.CreateBff();
        host.ConfigureClusters = clusters => clusters.Add(new()
        {
            ClusterId = "native",
            Destinations = new Dictionary<string, DestinationConfig>
            { ["primary"] = new() { Address = native.Urls[0] } }
        });
        host.ConfigureRoutes = routes => routes.Add(new() { RouteId = "native", ClusterId = "native", Match = new() { Hosts = ["native.example"], Path = "/legacy/{**path}" } });
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync("https://native.example/legacy/status", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe("native-upstream");
    }

    [Theory]
    [InlineData("http")]
    [InlineData("certificate-validation-disabled")]
    public async Task Startup_ManagedBffClusterIsInsecure_RejectsBeforeRequests(string defect)
    {
        // Arrange
        using var identity = new OidcWireMock(infrastructure);
        await using var host = identity.CreateBff();
        host.ConfigureClusters = clusters => clusters[0] = defect == "http"
            ? clusters[0] with { Destinations = new Dictionary<string, DestinationConfig> { ["primary"] = new() { Address = "http://127.0.0.1:8000/" } } }
            : clusters[0] with { HttpClient = new() { DangerousAcceptAnyServerCertificate = true } };

        // Act
        var error = Should.Throw<InvalidOperationException>(() => host.CreateBrowser());

        // Assert
        error.Message.ShouldContain("configuration");
        identity.BackendRequests.ShouldBeEmpty();
    }
}
