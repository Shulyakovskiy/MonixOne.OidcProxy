using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using CookieHeaderValue = Microsoft.Net.Http.Headers.CookieHeaderValue;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;
using MonixOne.OidcProxy.Sessions;
using StackExchange.Redis;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace MonixOne.OidcProxy.ModuleInitializers;

public static class ModuleInitializer
{
    public static IServiceCollection AddOidcProxy(this IServiceCollection services, ProxyOptions options,
        Action<OpenIdConnectOptions> configureOidc)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configureOidc);
        options.Validate();
        if (services.Any(d => d.ServiceType == typeof(ProxyOptions)))
            throw new InvalidOperationException("Register one OidcProxy per application host.");

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SessionStore>();
        services.AddSingleton<TokenSession>();
        var protection = services.AddDataProtection().SetApplicationName($"MonixOne.OidcProxy.v6:{options.CookieName}:{options.RedisKeyPrefix}");
        if (options.Redis is not null)
            protection.PersistKeysToStackExchangeRedis(options.Redis, options.RedisKeyPrefix + "data-protection");

        services.AddAuthorization(authorization => authorization.AddPolicy(BffConstants.AuthorizationPolicy,
            policy => policy.RequireAuthenticatedUser()));
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = options.CookieName + ".csrf";
            antiforgery.Cookie.Path = "/";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Lax;
        });
        services.AddAuthentication()
            .AddCookie(BffConstants.CookieScheme, cookie =>
            {
                cookie.Cookie.Name = options.CookieName;
                cookie.Cookie.Path = "/";
                cookie.Cookie.Domain = null;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.IsEssential = true;
                cookie.ExpireTimeSpan = options.SessionAbsoluteTimeout;
                cookie.SlidingExpiration = false;
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                cookie.Events.OnValidatePrincipal = async context =>
                {
                    var id = context.Principal?.FindFirstValue(BffConstants.SessionClaim);
                    try
                    {
                        var snapshot = id is null ? null : await context.HttpContext.RequestServices
                            .GetRequiredService<SessionStore>().ReadAsync(id, context.HttpContext.RequestAborted);
                        if (snapshot is null) context.RejectPrincipal();
                        else context.ReplacePrincipal(WithSessionId(snapshot.State.ReadTicket().Principal, id!));
                    }
                    catch (Exception e) when (e is RedisException or SessionUnavailableException)
                    {
                        context.HttpContext.Items[typeof(SessionUnavailableException)] = true;
                        context.RejectPrincipal();
                    }
                };
                cookie.Events.OnSigningOut = async context =>
                {
                    var id = context.HttpContext.User.FindFirstValue(BffConstants.SessionClaim);
                    if (id is not null)
                        await context.HttpContext.RequestServices.GetRequiredService<SessionStore>()
                            .RemoveAsync(id, context.HttpContext.RequestAborted);
                };
            })
            .AddOpenIdConnect(BffConstants.OidcScheme, oidc =>
            {
                oidc.SignInScheme = BffConstants.CookieScheme;
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.ResponseMode = OpenIdConnectResponseMode.Query;
                oidc.UsePkce = true;
                oidc.SaveTokens = true;
                oidc.MapInboundClaims = false;
                oidc.GetClaimsFromUserInfoEndpoint = false;
                oidc.RequireHttpsMetadata = true;
                oidc.CallbackPath = options.CallbackPath;
                oidc.SignedOutCallbackPath = $"/{options.EndpointName}/signout-callback";
                oidc.RemoteSignOutPath = PathString.Empty;
                oidc.BackchannelTimeout = TimeSpan.FromSeconds(10);
                oidc.TokenValidationParameters.NameClaimType = options.NameClaim;
                oidc.TokenValidationParameters.RoleClaimType = options.RoleClaim;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("offline_access");
                configureOidc(oidc);
                ConfigureProtocolEvents(oidc, options);
            });

        if (options.Mode != Mode.AuthenticateOnly)
        {
            // YARP 2.3 registers some policies more than once if AddReverseProxy is
            // repeated. Reuse an existing gateway registration and its providers.
            IReverseProxyBuilder yarp = services.Any(d => d.ServiceType == typeof(IProxyStateLookup))
                ? new ExistingYarpBuilder(services) : services.AddReverseProxy();
            yarp.AddTransforms(builder =>
            {
                var isBff = IsBffRoute(builder.Route, options);
                builder.AddRequestTransform(context =>
                {
                    if (!isBff)
                    {
                        if (context.ProxyRequest.Headers.TryGetValues("Cookie", out var values))
                        {
                            context.ProxyRequest.Headers.Remove("Cookie");
                            if (CookieHeaderValue.TryParseList(values.ToList(), out var cookies))
                            {
                                var kept = cookies.Where(c => !IsBffCookie(c.Name.ToString(), options.CookieName)).ToArray();
                                if (kept.Length > 0)
                                    context.ProxyRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", kept.Select(c => c.ToString())));
                            }
                        }
                        return ValueTask.CompletedTask;
                    }
                    context.ProxyRequest.Headers.Remove("Cookie");
                    context.ProxyRequest.Headers.Authorization = null;
                    var ticket = context.HttpContext.Items[BffConstants.TicketItem] as AuthenticationTicket;
                    var token = ticket?.Properties.GetTokenValue("access_token");
                    if (!string.IsNullOrEmpty(token))
                        context.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return ValueTask.CompletedTask;
                });
                builder.AddResponseTransform(context =>
                {
                    // Downstream services cannot create or replace a BFF session cookie.
                    var headers = context.HttpContext.Response.Headers;
                    if (isBff) headers.Remove("Set-Cookie");
                    else if (headers.TryGetValue("Set-Cookie", out var values))
                    {
                        headers.Remove("Set-Cookie");
                        if (SetCookieHeaderValue.TryParseList(values.Select(v => v!).ToList(), out var cookies))
                            foreach (var cookie in cookies.Where(c => !IsBffCookie(c.Name.ToString(), options.CookieName)))
                                headers.Append("Set-Cookie", cookie.ToString());
                    }
                    return ValueTask.CompletedTask;
                });
            });
            foreach (var configure in options.YarpConfiguration) configure(yarp);
        }
        return services;
    }

    private static void ConfigureProtocolEvents(OpenIdConnectOptions oidc, ProxyOptions options)
    {
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.SaveTokens = true;
        oidc.MapInboundClaims = false;
        oidc.RequireHttpsMetadata = true;
        oidc.CallbackPath = options.CallbackPath;
        oidc.TokenValidationParameters.RequireSignedTokens = true;
        oidc.TokenValidationParameters.ValidateIssuerSigningKey = true;
        oidc.TokenValidationParameters.ValidateIssuer = true;
        oidc.TokenValidationParameters.ValidateAudience = true;
        oidc.TokenValidationParameters.ValidateLifetime = true;
        oidc.ProtocolValidator.RequireNonce = true;
        var validated = oidc.Events.OnTokenValidated;
        oidc.Events.OnTokenValidated = async context =>
        {
            // ASP.NET Core permits unsigned ID tokens from the HTTPS token endpoint
            // in code flow, as allowed by OIDC. This fork requires signed ID tokens.
            // The handler already verifies the signature whenever one is present.
            var raw = context.TokenEndpointResponse?.IdToken ?? context.ProtocolMessage.IdToken;
            if (string.IsNullOrEmpty(raw) || !new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(raw).IsSigned)
            {
                context.Fail("An ID token with a validated signature is required.");
                return;
            }
            await validated(context);
        };
        var redirect = oidc.Events.OnRedirectToIdentityProvider;
        oidc.Events.OnRedirectToIdentityProvider = async context =>
        {
            await redirect(context);
            context.ProtocolMessage.RedirectUri = new Uri(options.PublicOrigin!, options.CallbackPath).AbsoluteUri;
        };
        oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = oidc.ClientId;
            context.ProtocolMessage.PostLogoutRedirectUri = new Uri(options.PublicOrigin!, oidc.SignedOutCallbackPath).AbsoluteUri;
            context.ProtocolMessage.IdTokenHint = context.HttpContext.Items[BffConstants.LogoutIdTokenItem] as string;
            return Task.CompletedTask;
        };
        var tokenResponse = oidc.Events.OnTokenResponseReceived;
        oidc.Events.OnTokenResponseReceived = async context =>
        {
            await tokenResponse(context);
            if (string.IsNullOrWhiteSpace(context.TokenEndpointResponse.AccessToken) ||
                !string.Equals(context.TokenEndpointResponse.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(context.TokenEndpointResponse.ExpiresIn, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                seconds <= 0 || seconds > int.MaxValue)
                context.Fail("Invalid token endpoint response.");
        };
        oidc.Events.OnTicketReceived = async context =>
        {
            var services = context.HttpContext.RequestServices;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            var store = services.GetRequiredService<SessionStore>();
            var properties = context.Properties!;
            var fullTicket = new AuthenticationTicket(context.Principal!, properties, BffConstants.CookieScheme);
            string id;
            try
            {
                id = await store.CreateAsync(fullTicket, context.HttpContext.RequestAborted);
            }
            catch
            {
                await services.GetRequiredService<TokenSession>().RevokeAsync(fullTicket, CancellationToken.None);
                throw;
            }

            var previous = await context.HttpContext.AuthenticateAsync(BffConstants.CookieScheme);
            var previousId = previous.Principal?.FindFirstValue(BffConstants.SessionClaim);
            if (previousId is not null) await store.RemoveAsync(previousId, context.HttpContext.RequestAborted);

            // Only an opaque session reference enters the framework-protected cookie.
            // Access, refresh and ID tokens remain in the protected server-side record.
            context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(BffConstants.SessionClaim, id)], BffConstants.CookieScheme));
            properties.Items.Clear();
            properties.IssuedUtc = now;
            properties.ExpiresUtc = now + options.SessionAbsoluteTimeout;
            properties.IsPersistent = false;
        };
        oidc.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            for (var failure = context.Failure; failure is not null; failure = failure.InnerException)
                if (failure is RedisException or SessionUnavailableException)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                }
            context.Response.Headers.CacheControl = "no-store";
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MonixOne.OidcProxy.Oidc").LogWarning("OIDC sign-in failed. Failure type: {FailureType}", context.Failure?.GetType().Name);
            return Task.CompletedTask;
        };
    }

    public static WebApplication UseOidcProxy(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ProxyOptions>();
        app.UseRouting();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.Contains(BffConstants.ApiMetadata) != true)
            {
                await next(context);
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            var origin = context.Request.Headers.Origin;
            var fetchSite = context.Request.Headers["Sec-Fetch-Site"];
            if (origin.Count > 1 || origin.Count == 1 && origin[0] != options.PublicOrigin!.GetLeftPart(UriPartial.Authority) ||
                fetchSite.Count > 0 && fetchSite != "same-origin")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            // A principal established by another host middleware/scheme is not a BFF session.
            var authentication = await context.AuthenticateAsync(BffConstants.CookieScheme);
            context.User = authentication.Principal ?? new ClaimsPrincipal(new ClaimsIdentity());
            if (context.Items.ContainsKey(typeof(SessionUnavailableException)))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            var hasCsrfHeader = context.Request.Headers["X-CSRF"].Count == 1 && context.Request.Headers["X-CSRF"] == "1";
            if (!hasCsrfHeader)
            {
                if (!HttpMethods.IsPost(context.Request.Method) ||
                    context.Request.Path != $"/{options.EndpointName}/end-session" || !context.Request.HasFormContentType)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            if (!authentication.Succeeded)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            try
            {
                var id = context.User.FindFirstValue(BffConstants.SessionClaim);
                if (id is not null && context.Request.Path != $"/{options.EndpointName}/end-session")
                {
                    var ticket = await context.RequestServices.GetRequiredService<TokenSession>()
                        .GetActiveTicketAsync(id, context.RequestAborted);
                    if (ticket is null)
                    {
                        await context.SignOutAsync(BffConstants.CookieScheme);
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    context.Items[BffConstants.TicketItem] = ticket;
                    context.User = WithSessionId(ticket.Principal, id);
                }
                // Authorization must use this BFF session and its refreshed claims rather than
                // a cached result from the host's default Bearer/Identity authentication scheme.
                context.Features.Set<IAuthenticateResultFeature>(new BffAuthenticateResultFeature
                {
                    AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(
                        context.User, authentication.Properties!, BffConstants.CookieScheme))
                });
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is RedisException or SessionUnavailableException)
            {
                if (context.Response.HasStarted) throw;
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MonixOne.OidcProxy.Session")
                    .LogError("BFF session unavailable. Failure type: {FailureType}", e.GetType().Name);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });
        app.UseAuthorization();

        app.MapGet($"/{options.EndpointName}/login", (HttpContext context) =>
        {
            var requested = context.Request.Query["landingpage"];
            var path = options.LandingPage;
            if (requested.Count != 0)
            {
                if (requested.Count != 1 || !options.EnableUserPreferredLandingPages ||
                    !LandingPage.TryParse(requested[0]!, out _) ||
                    options.AllowedLandingPages.Length > 0 && !options.AllowedLandingPages.Contains(requested[0], StringComparer.Ordinal))
                    return Results.BadRequest();
                path = requested[0]!;
            }
            return Results.Challenge(new AuthenticationProperties { RedirectUri = path }, [BffConstants.OidcScheme]);
        }).AllowAnonymous();

        app.MapGet($"/{options.EndpointName}/me", (HttpContext context) =>
            Results.Json(context.User.Claims.Where(c => c.Type != BffConstants.SessionClaim)
                .GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray())))
            .AsOidcProxyApi();

        app.MapGet($"/{options.EndpointName}/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new { tokens.RequestToken, tokens.FormFieldName });
        }).AsOidcProxyApi();

        app.MapPost($"/{options.EndpointName}/end-session", async (HttpContext context, SessionStore store, TokenSession tokens) =>
        {
            var id = context.User.FindFirstValue(BffConstants.SessionClaim)!;
            // Once logout is authorized, a browser disconnect must not interrupt
            // its local invalidation. GETDEL returns the tokens current at deletion,
            // including a refresh that committed after authentication of this request.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ticket = await store.TakeAsync(id, timeout.Token);
            await context.SignOutAsync(BffConstants.CookieScheme);
            if (ticket is not null)
            {
                context.Items[BffConstants.LogoutIdTokenItem] = ticket.Properties.GetTokenValue("id_token");
                await tokens.RevokeAsync(ticket, CancellationToken.None);
            }
            return Results.SignOut(new AuthenticationProperties { RedirectUri = options.LandingPage }, [BffConstants.OidcScheme]);
        }).AsOidcProxyApi();

        if (options.Mode != Mode.AuthenticateOnly)
            app.MapReverseProxy().Add(endpoint =>
            {
                var route = endpoint.Metadata.OfType<RouteModel>().Single().Config;
                if (!IsBffRoute(route, options)) return;
                endpoint.Metadata.Add(new AuthorizeAttribute(BffConstants.AuthorizationPolicy));
                endpoint.Metadata.Add(BffConstants.ApiMetadata);
            });
        return app;
    }

    private static bool IsBffRoute(RouteConfig route, ProxyOptions options)
    {
        if (!options.ProtectOnlyMarkedRoutes) return true;
        if (route.Metadata?.TryGetValue(ProxyOptions.BffRouteMetadataKey, out var value) != true) return false;
        return bool.TryParse(value, out var enabled) ? enabled
            : throw new ArgumentException($"Route '{route.RouteId}' has an invalid {ProxyOptions.BffRouteMetadataKey} value; use true or false.");
    }

    private static bool IsBffCookie(string name, string cookieName)
    {
        if (name == cookieName || name == cookieName + ".csrf") return true;
        var chunkPrefix = cookieName + "C";
        return name.StartsWith(chunkPrefix, StringComparison.Ordinal) && name.Length > chunkPrefix.Length &&
               name.AsSpan(chunkPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static ClaimsPrincipal WithSessionId(ClaimsPrincipal principal, string id)
    {
        var clone = principal.Clone();
        ((ClaimsIdentity)clone.Identity!).AddClaim(new Claim(BffConstants.SessionClaim, id));
        return clone;
    }

    public static TBuilder AsOidcProxyApi<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(BffConstants.AuthorizationPolicy).WithMetadata(BffConstants.ApiMetadata);
}

internal sealed class BffAuthenticateResultFeature : IAuthenticateResultFeature
{
    public AuthenticateResult? AuthenticateResult { get; set; }
}

internal sealed class ExistingYarpBuilder(IServiceCollection services) : IReverseProxyBuilder
{
    public IServiceCollection Services { get; } = services;
}
