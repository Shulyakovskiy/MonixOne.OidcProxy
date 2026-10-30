using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace MonixOne.OidcProxy.Yarp.Tests;

public sealed class ConfigurationCompositionTests
{
    [Theory]
    [InlineData("https://browser.example", true, true)]
    [InlineData("https://browser.example", false, false)]
    [InlineData("https://browser.example.untrusted.example", true, false)]
    [InlineData("https://browser.example:444", true, false)]
    [InlineData("null", true, false)]
    public async Task Cors_PreflightUsesConfiguredOriginsWithoutBackendOrRedis(
        string origin,
        bool configureOrigin,
        bool allowed
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" }
        );
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        var values = new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "api",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**path}",
            ["ReverseProxy:Clusters:api:Destinations:primary:Address"] = "http://127.0.0.1:1",
        };
        if (configureOrigin)
            values["Cors:Origins:0"] = "https://browser.example";
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddOidcProxy(builder.Configuration);
        await using var app = builder.Build();
        app.UseOidcProxy(pipeline => pipeline.UseCors(OidcProxyDefaults.CorsPolicy));
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/status");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Content-Type,X-CSRF");

        using var response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBe(allowed);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBe(allowed);
        if (allowed)
            response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(origin);
    }

    [Fact]
    public async Task ConfiguredSpa_InvalidOriginStillFailsValidation()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" }
        );
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Bff:ApplicationName"] = "configuration-test",
                ["Bff:EncryptionKey"] = "configuration-test-fixture-encryption-secret",
                ["Bff:Clients:spa:PublicOrigin"] = "http://spa.example",
                ["Bff:Clients:spa:OpenIdConnect:Authority"] = "https://identity.example",
                ["Bff:Clients:spa:OpenIdConnect:ClientId"] = "fixture-client",
                ["Bff:Clients:spa:OpenIdConnect:ClientSecret"] = "fixture-secret",
                ["ReverseProxy:Routes:api:ClusterId"] = "api",
                ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**path}",
                ["ReverseProxy:Clusters:api:Destinations:primary:Address"] = "http://127.0.0.1:1",
            }
        );
        builder.Services.AddOidcProxy(builder.Configuration);
        await using var app = builder.Build();

        var error = Should.Throw<OptionsValidationException>(() => app.UseOidcProxy());

        error.Message.ShouldContain("PublicOrigin");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Bff\":{}}")]
    [InlineData("{\"Bff\":{\"Clients\":{}}}")]
    [InlineData(
        "{\"Bff\":{\"Clients\":{\"unused\":{\"OpenIdConnect\":{\"ClientSecret\":\"unused-secret\"}}}}}"
    )]
    public async Task WithoutSpaProfile_NativeRoutesWorkWithoutRedisOrBffSecrets(string json)
    {
        var ct = TestContext.Current.CancellationToken;
        using var backend = WireMockServer.Start();
        backend
            .Given(Request.Create().WithPath("/api/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("native-service"));
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" }
        );
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ReverseProxy:Routes:api:ClusterId"] = "api",
                ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**path}",
                ["ReverseProxy:Clusters:api:Destinations:primary:Address"] = backend.Urls[0],
            }
        );
        builder.Services.AddOidcProxy(builder.Configuration);
        await using var app = builder.Build();
        app.UseOidcProxy();
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer native-token");

        using var response = await client.GetAsync("/api/status?version=1", ct);
        using var bff = await client.GetAsync("/.auth/me", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe("native-service");
        backend
            .LogEntries.Single()
            .RequestMessage!.Headers!["Authorization"]
            .Single()
            .ShouldBe("Bearer native-token");
        bff.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        app.Services.GetRequiredService<IAuthenticationSchemeProvider>()
            .ShouldBeOfType<AuthenticationSchemeProvider>();
    }
}
