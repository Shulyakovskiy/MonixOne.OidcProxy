using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

namespace MonixOne.OidcProxy.Yarp.Tests;

/// <summary>
/// Runs actual OpenIddict and a protected resource API on HTTPS/Kestrel. Its
/// database uses the official OpenIddict EF model in disposable PostgreSQL;
/// the BFF package itself owns no PostgreSQL schema or identity-provider logic.
/// </summary>
public sealed class OpenIddictHost(InfrastructureFixture infrastructure) : IAsyncLifetime
{
    private WebApplication _app = null!;
    public Uri Origin { get; private set; } = null!;
    public ConcurrentDictionary<string, int> RefreshRequests { get; } = new();
    public ConcurrentQueue<string> ResourceTokens { get; } = new();
    public const string Secret = "real-openiddict-test-secret";
    public const string Subject = "user-1";

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(
                IPAddress.Loopback,
                0,
                listen => listen.UseHttps(infrastructure.Certificate)
            )
        );
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddDbContext<OpenIddictDbContext>(options =>
            options.UseNpgsql(infrastructure.PostgreSql.GetConnectionString()).UseOpenIddict()
        );
        builder
            .Services.AddAuthentication("identity-session")
            .AddCookie(
                "identity-session",
                cookie =>
                {
                    cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    cookie.Events.OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    };
                }
            );
        builder
            .Services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<OpenIddictDbContext>())
            .AddServer(server =>
            {
                // Keep encrypted access tokens enabled: forwarding must remain opaque.
                // The dynamic HTTPS port makes the standard request issuer appropriate here.
                server
                    .SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    .SetRevocationEndpointUris("/connect/revocation")
                    .SetEndSessionEndpointUris("/connect/logout")
                    .SetJsonWebKeySetEndpointUris("/.well-known/jwks")
                    .AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange()
                    .SetAccessTokenLifetime(TimeSpan.FromMinutes(2))
                    .SetRefreshTokenLifetime(TimeSpan.FromHours(1))
                    .DisableSlidingRefreshTokenExpiration()
                    .SetRefreshTokenReuseLeeway(TimeSpan.Zero)
                    .RegisterScopes("api.read")
                    .AddEphemeralSigningKey()
                    .AddEphemeralEncryptionKey();
                server
                    .UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.AddAudiences("resource-api");
                validation.UseAspNetCore();
            });
        builder.Services.AddAuthorization(authorization =>
            authorization.AddPolicy(
                "api-read",
                policy =>
                    policy
                        .AddAuthenticationSchemes(
                            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                        )
                        .RequireAuthenticatedUser()
                        .RequireAssertion(context => context.User.HasScope("api.read"))
            )
        );

        _app = builder.Build();
        _app.Use(
            async (context, next) =>
            {
                if (context.Request.Path == "/connect/token" && context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    if (form["grant_type"] == "refresh_token")
                        RefreshRequests.AddOrUpdate(
                            form["client_id"].ToString(),
                            1,
                            (_, count) => count + 1
                        );
                }
                await next(context);
            }
        );
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapPost(
            "/test/session",
            () =>
                Results.SignIn(
                    new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim("sub", Subject)], "identity-session")
                    ),
                    authenticationScheme: "identity-session"
                )
        );
        _app.MapGet(
            "/connect/authorize",
            async (HttpContext context) =>
            {
                var session = await context.AuthenticateAsync("identity-session");
                if (!session.Succeeded)
                    return Results.Challenge(authenticationSchemes: ["identity-session"]);
                var request = context.GetOpenIddictServerRequest()!;
                var identity = new ClaimsIdentity("identity-session", "name", "role");
                identity.AddClaim(new Claim("sub", Subject));
                identity.AddClaim(
                    new Claim("role", "reader").SetDestinations(
                        OpenIddictConstants.Destinations.AccessToken
                    )
                );
                var principal = new ClaimsPrincipal(identity);
                principal.SetScopes(request.GetScopes());
                principal.SetResources("resource-api");
                return Results.SignIn(
                    principal,
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
                );
            }
        );
        _app.MapGet(
            "/connect/logout",
            () =>
                Results.SignOut(
                    authenticationSchemes:
                    [
                        "identity-session",
                        OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    ]
                )
        );
        _app.MapGet(
                "/api/status",
                (HttpContext context) =>
                {
                    ResourceTokens.Enqueue(context.Request.Headers.Authorization.ToString());
                    return Results.Json(
                        new
                        {
                            subject = context.User.GetClaim("sub"),
                            scopes = context.User.GetScopes(),
                            roles = context.User.GetClaims("role"),
                        }
                    );
                }
            )
            .RequireAuthorization("api-read");

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope
                .ServiceProvider.GetRequiredService<OpenIddictDbContext>()
                .Database.EnsureCreatedAsync();
        await _app.StartAsync();
        var address = _app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        Origin = new Uri(address);
    }

    public async Task<string> RegisterClientAsync(
        bool publicClient = false,
        Uri? origin = null,
        string pathBase = ""
    )
    {
        var id = "spa-" + Guid.NewGuid().ToString("N"); // Isolates token/application state between tests.
        await using var scope = _app.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>()
            .CreateAsync(
                new OpenIddictApplicationDescriptor
                {
                    ClientId = id,
                    ClientSecret = publicClient ? null : Secret,
                    ClientType = publicClient
                        ? OpenIddictConstants.ClientTypes.Public
                        : OpenIddictConstants.ClientTypes.Confidential,
                    ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
                    RedirectUris =
                    {
                        new Uri(
                            origin ?? BffApplicationFactory.Origin,
                            pathBase + OidcProxyRoutes.LoginCallback
                        ),
                    },
                    PostLogoutRedirectUris =
                    {
                        new Uri(
                            origin ?? BffApplicationFactory.Origin,
                            pathBase + OidcProxyRoutes.LogoutCallback
                        ),
                    },
                    Permissions =
                    {
                        OpenIddictConstants.Permissions.Endpoints.Authorization,
                        OpenIddictConstants.Permissions.Endpoints.Token,
                        OpenIddictConstants.Permissions.Endpoints.Revocation,
                        OpenIddictConstants.Permissions.Endpoints.EndSession,
                        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                        OpenIddictConstants.Permissions.ResponseTypes.Code,
                        OpenIddictConstants.Permissions.Prefixes.Scope + "api.read",
                    },
                }
            );
        return id;
    }

    public HttpClient CreateBrowser() =>
        new(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate?.Thumbprint == infrastructure.Certificate.Thumbprint,
            }
        )
        {
            BaseAddress = Origin,
        };

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed class OpenIddictDbContext(DbContextOptions<OpenIddictDbContext> options)
        : DbContext(options);
}
