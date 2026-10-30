using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class PipelineCompositionTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task BffStartup_ConflictingRedisRegistrationsFailBeforeRequests()
    {
        using var primary = await ConnectionMultiplexer.ConnectAsync(
            infrastructure.Redis.GetConnectionString()
        );
        using var other = await ConnectionMultiplexer.ConnectAsync(
            infrastructure.Redis.GetConnectionString()
        );
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" }
        );
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Bff:ApplicationName"] = "redis-composition-test",
                ["Bff:EncryptionKey"] = infrastructure.EncryptionKey,
                ["Bff:Clients:spa:PublicOrigin"] = "https://spa.example",
                ["Bff:Clients:spa:OpenIdConnect:Authority"] = "https://identity.example",
                ["Bff:Clients:spa:OpenIdConnect:ClientId"] = "fixture-client",
                ["Bff:Clients:spa:OpenIdConnect:ClientSecret"] = "fixture-secret",
                ["ReverseProxy:Routes:api:ClusterId"] = "api",
                ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**path}",
                ["ReverseProxy:Clusters:api:Destinations:primary:Address"] = "http://127.0.0.1:1",
            }
        );
        builder.Services.AddSingleton(primary);
        builder.Services.AddSingleton<IConnectionMultiplexer>(other);
        var error = Should.Throw<InvalidOperationException>(() =>
            builder.Services.AddOidcProxy(builder.Configuration)
        );

        error.Message.ShouldContain("same shared connection");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redis_PreRegisteredConnectionIsSharedByBothServiceTypes(
        bool registerInterface
    )
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(
            infrastructure.Redis.GetConnectionString()
        );
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ReverseProxy:Routes:api:ClusterId"] = "api",
                ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**path}",
                ["ReverseProxy:Clusters:api:Destinations:primary:Address"] = "http://127.0.0.1:1",
            }
        );
        if (registerInterface)
            builder.Services.AddSingleton<IConnectionMultiplexer>(connection);
        else
            builder.Services.AddSingleton(connection);
        builder.Services.AddOidcProxy(builder.Configuration);
        await using (var app = builder.Build())
        {
            app.Services.GetRequiredService<IConnectionMultiplexer>().ShouldBeSameAs(connection);
            app.Services.GetRequiredService<ConnectionMultiplexer>().ShouldBeSameAs(connection);
        }
        connection.IsConnected.ShouldBeTrue();
        await connection.GetDatabase().PingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_RateLimitRunsAfterRoutingAndBeforeAuthentication(bool enableBff)
    {
        // Arrange: exercise the public pipeline extension with a real native YARP route.
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
        var values = new Dictionary<string, string?>
        {
            ["Bff:RedisConnectionStringKey"] = "Gateway:Redis",
            ["Gateway:Redis"] = infrastructure.Redis.GetConnectionString(),
            ["ForwardedHeaders:KnownProxies:0"] = "10.20.30.40",
            ["ReverseProxy:Routes:api:ClusterId"] = "api",
            ["ReverseProxy:Routes:api:RateLimiterPolicy"] = "bounded",
            ["ReverseProxy:Routes:api:Match:Hosts:0"] = "spa.example",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/status",
            ["ReverseProxy:Clusters:api:Destinations:primary:Address"] = backend.Urls[0],
        };
        if (enableBff)
        {
            values["Bff:ApplicationName"] = "pipeline-test";
            values["Bff:KeyNamespace"] = Guid.NewGuid().ToString("N");
            values["Bff:EncryptionKey"] = infrastructure.EncryptionKey;
            values["Bff:Clients:spa:PublicOrigin"] = "https://spa.example";
            values["Bff:Clients:spa:PathBase"] = "/admin";
            values["Bff:Clients:spa:OpenIdConnect:Authority"] = "https://identity.example";
            values["Bff:Clients:spa:OpenIdConnect:ClientId"] = "pipeline-client";
            values["Bff:Clients:spa:OpenIdConnect:ClientSecret"] = "pipeline-fixture-secret";
        }
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(
                "bounded",
                policy =>
                {
                    policy.PermitLimit = 1;
                    policy.Window = TimeSpan.FromHours(1);
                    policy.QueueLimit = 0;
                }
            );
        });
        builder.Services.AddOidcProxy(builder.Configuration);
        await using var app = builder.Build();
        app.Use(
            (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.30.40");
                return next(context);
            }
        );
        (string Scheme, string PathBase, string? Route)? observed = null;
        app.UseOidcProxy(pipeline =>
        {
            pipeline.Use(
                (context, next) =>
                {
                    observed = (
                        context.Request.Scheme,
                        context.Request.PathBase.Value ?? "",
                        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                    );
                    return next(context);
                }
            );
            pipeline.UseRateLimiter();
        });
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "spa.example");
        client.DefaultRequestHeaders.Add("X-CSRF", "1");
        var path = (enableBff ? "/admin" : "") + "/api/status";

        // Act: even a rejected BFF request consumes the endpoint's rate budget.
        using var first = await client.GetAsync(path, ct);
        using var second = await client.GetAsync(path, ct);

        // Assert
        first.StatusCode.ShouldBe(enableBff ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        observed.ShouldNotBeNull();
        observed.Value.Scheme.ShouldBe("https");
        observed.Value.PathBase.ShouldBe(enableBff ? "/admin" : "");
        observed.Value.Route.ShouldBe("/api/status");
        backend.LogEntries.Count.ShouldBe(enableBff ? 0 : 1);
        app.Services.GetRequiredService<IConnectionMultiplexer>()
            .ShouldBeSameAs(app.Services.GetRequiredService<ConnectionMultiplexer>());
    }
}
