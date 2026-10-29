using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Authentication;

internal static class OidcAuthentication
{
    internal static void Register(IServiceCollection services, string name, Action<OpenIdConnectOptions> configureOidc)
    {
        services.AddAuthentication()
            .AddCookie(OidcProxyDefaults.CookieScheme(name), cookie =>
            {
                cookie.Cookie.Name = OidcProxyDefaults.CookieName(name);
                cookie.Cookie.Path = "/";
                cookie.Cookie.Domain = null;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.IsEssential = true;
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
                    if (context.HttpContext.RequestServices.GetRequiredService<BffClients>().Find(context.Request)?.Name != name)
                    {
                        context.RejectPrincipal();
                        return;
                    }
                    var id = context.Principal?.FindFirstValue(BffConstants.SessionClaim);
                    try
                    {
                        var snapshot = id is null ? null : await context.HttpContext.RequestServices
                            .GetRequiredKeyedService<SessionStore>(name).ReadAsync(id, context.HttpContext.RequestAborted);
                        if (snapshot is null) context.RejectPrincipal();
                        else
                        {
                            context.ReplacePrincipal(SessionPrincipal.WithSessionId(snapshot.State.ReadTicket().Principal, id!));
                            context.HttpContext.Items[BffConstants.SessionSnapshotItem] = (name, snapshot);
                        }
                    }
                    catch (Exception e) when (e is RedisException or RedisTimeoutException or SessionUnavailableException)
                    {
                        context.HttpContext.Items[typeof(SessionUnavailableException)] = true;
                        context.RejectPrincipal();
                    }
                };
                cookie.Events.OnSigningOut = async context =>
                {
                    var id = context.HttpContext.User.FindFirstValue(BffConstants.SessionClaim);
                    if (id is not null && !Equals(context.HttpContext.Items[BffConstants.RemovedSessionItem], (name, id)))
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try
                        {
                            await context.HttpContext.RequestServices.GetRequiredKeyedService<SessionStore>(name).RemoveAsync(id, timeout.Token);
                        }
                        catch (OperationCanceledException e) when (timeout.IsCancellationRequested)
                        {
                            throw new SessionUnavailableException("Unable to confirm session invalidation within its server budget.", e);
                        }
                    }
                };
            })
            .AddOpenIdConnect(OidcProxyDefaults.OpenIdConnectScheme(name), oidc =>
            {
                oidc.TokenValidationParameters.NameClaimType = "name";
                oidc.TokenValidationParameters.RoleClaimType = "role";
                configureOidc(oidc);
            });
        services.AddOptions<CookieAuthenticationOptions>(OidcProxyDefaults.CookieScheme(name))
            .Configure<IOptionsMonitor<OidcProxyClientOptions>>((cookie, options) => cookie.ExpireTimeSpan = options.Get(name).SessionAbsoluteTimeout);
        services.AddOptions<OpenIdConnectOptions>(OidcProxyDefaults.OpenIdConnectScheme(name))
            .Configure<IOptionsMonitor<OidcProxyClientOptions>>((oidc, options) => ConfigureProtocolEvents(oidc, name, options.Get(name)))
            .Validate(o => Uri.TryCreate(o.Authority, UriKind.Absolute, out var authority) && authority.Scheme == "https" &&
                authority.UserInfo.Length == 0 && authority.Query.Length == 0 && authority.Fragment.Length == 0,
                "An HTTPS OIDC Authority without credentials, query or fragment is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId) && !string.IsNullOrWhiteSpace(o.ClientSecret),
                "A confidential OIDC ClientId and ClientSecret are required.")
            .ValidateOnStart();
    }

    private static void ConfigureProtocolEvents(OpenIdConnectOptions oidc, string name, OidcProxyClientOptions options)
    {
        oidc.SignInScheme = OidcProxyDefaults.CookieScheme(name);
        oidc.SignedOutCallbackPath = OidcProxyRoutes.LogoutCallback;
        oidc.RemoteSignOutPath = PathString.Empty;
        oidc.BackchannelTimeout = TimeSpan.FromSeconds(10);
        oidc.Scope.Add("openid");
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.SaveTokens = true;
        oidc.MapInboundClaims = false;
        oidc.RequireHttpsMetadata = true;
        oidc.CallbackPath = OidcProxyRoutes.LoginCallback;
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
            if (string.IsNullOrEmpty((context.SecurityToken.InnerToken ?? context.SecurityToken).RawSignature))
            {
                context.Fail("An ID token with a validated signature is required.");
                return;
            }
            // Preserve protocol values before framework claim actions remove nonce.
            // The validated inner token also supports signed-and-encrypted ID tokens.
            context.Properties!.Items[BffConstants.OriginalNonce] = context.SecurityToken.Payload.Nonce;
            if (context.SecurityToken.Payload.TryGetValue("auth_time", out var authTime))
                context.Properties.Items[BffConstants.OriginalAuthTime] = Convert.ToString(authTime, CultureInfo.InvariantCulture);
            await validated(context);
        };
        var redirect = oidc.Events.OnRedirectToIdentityProvider;
        oidc.Events.OnRedirectToIdentityProvider = async context =>
        {
            await redirect(context);
            context.ProtocolMessage.RedirectUri = UriHelper.BuildAbsolute(options.PublicOrigin!.Scheme,
                HostString.FromUriComponent(options.PublicOrigin.Authority), PathString.FromUriComponent(options.PathBase), OidcProxyRoutes.LoginCallback);
        };
        oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = oidc.ClientId;
            context.ProtocolMessage.PostLogoutRedirectUri = UriHelper.BuildAbsolute(options.PublicOrigin!.Scheme,
                HostString.FromUriComponent(options.PublicOrigin.Authority), PathString.FromUriComponent(options.PathBase), oidc.SignedOutCallbackPath);
            context.ProtocolMessage.IdTokenHint = context.HttpContext.Items[BffConstants.LogoutIdTokenItem] as string;
            return Task.CompletedTask;
        };
        var tokenResponse = oidc.Events.OnTokenResponseReceived;
        oidc.Events.OnTokenResponseReceived = async context =>
        {
            // The provider has already issued credentials. Retain the known refresh
            // token only for this request so a later protocol failure can revoke it.
            if (!string.IsNullOrEmpty(context.TokenEndpointResponse.RefreshToken))
                context.HttpContext.Items[BffConstants.SignInRefreshTokenItem] = context.TokenEndpointResponse.RefreshToken;
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
            var store = services.GetRequiredKeyedService<SessionStore>(name);
            // These operations can outlive an aborted request or host shutdown.
            // Capture cleanup dependencies before starting the server-owned SET.
            var tokens = services.GetRequiredKeyedService<TokenSession>(name);
            var sessionLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MonixOne.OidcProxy.Yarp.Session");
            var properties = context.Properties!;
            properties.Items.TryGetValue(BffConstants.PreviousSession, out var previousId);
            properties.Items.Remove(BffConstants.PreviousSession);
            var fullTicket = new AuthenticationTicket(context.Principal!, properties, OidcProxyDefaults.CookieScheme(name));
            // From here the ticket creation/cleanup block owns these credentials.
            context.HttpContext.Items.Remove(BffConstants.SignInRefreshTokenItem);
            string? id = null;
            try
            {
                id = await store.CreateAsync(fullTicket, context.HttpContext.RequestAborted);
                context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                if (previousId is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var previousTicket = await store.TakeAsync(previousId, timeout.Token);
                    if (previousTicket is not null)
                        await tokens.RevokeAsync(previousTicket, CancellationToken.None);
                }
                // The cookie receives only a framework-protected random reference.
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(BffConstants.SessionClaim, id)], OidcProxyDefaults.CookieScheme(name)));
                properties.Items.Clear();
                properties.IssuedUtc = now;
                properties.ExpiresUtc = now + options.SessionAbsoluteTimeout;
                properties.IsPersistent = false;
            }
            catch (Exception failure)
            {
                if (id is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await store.RemoveAsync(id, timeout.Token); }
                    catch (Exception e) when (e is RedisException or RedisTimeoutException or OperationCanceledException or ObjectDisposedException)
                    {
                        sessionLogger.LogError("Unable to remove failed sign-in session. Failure type: {FailureType}", e.GetType().Name);
                    }
                }
                await tokens.RevokeAsync(fullTicket, CancellationToken.None);
                if (failure is RedisException or RedisTimeoutException or SessionUnavailableException)
                {
                    // TicketReceived runs after the native remote-authentication
                    // failure boundary. Handle infrastructure failures here too.
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.CacheControl = "no-store";
                    sessionLogger.LogWarning("OIDC session creation failed. Failure type: {FailureType}", failure.GetType().Name);
                    return;
                }
                throw;
            }
        };

        oidc.Events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            if (context.HttpContext.Items.Remove(BffConstants.SignInRefreshTokenItem, out var issued) && issued is string refreshToken)
            {
                await context.HttpContext.RequestServices.GetRequiredKeyedService<TokenSession>(name)
                    .RevokeAsync(refreshToken, CancellationToken.None);
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            for (var failure = context.Failure; failure is not null; failure = failure.InnerException)
                if (failure is RedisException or RedisTimeoutException or SessionUnavailableException)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                }
            context.Response.Headers.CacheControl = "no-store";
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MonixOne.OidcProxy.Yarp.Oidc").LogWarning("OIDC sign-in failed. Failure type: {FailureType}", context.Failure?.GetType().Name);
        };
    }

}
