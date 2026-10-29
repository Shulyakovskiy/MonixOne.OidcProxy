using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Authentication;

internal static class OidcAuthentication
{
    internal static void Register(IServiceCollection services, Action<OpenIdConnectOptions> configureOidc)
    {
        services.AddAuthentication()
            .AddCookie(OidcProxyDefaults.CookieScheme, cookie =>
            {
                cookie.Cookie.Name = "__Host-Http-oidcproxy.session";
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
                    var id = context.Principal?.FindFirstValue(BffConstants.SessionClaim);
                    try
                    {
                        var snapshot = id is null ? null : await context.HttpContext.RequestServices
                            .GetRequiredService<SessionStore>().ReadAsync(id, context.HttpContext.RequestAborted);
                        if (snapshot is null) context.RejectPrincipal();
                        else context.ReplacePrincipal(SessionPrincipal.WithSessionId(snapshot.State.ReadTicket().Principal, id!));
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
                    if (id is not null)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await context.HttpContext.RequestServices.GetRequiredService<SessionStore>().RemoveAsync(id, timeout.Token);
                    }
                };
            })
            .AddOpenIdConnect(OidcProxyDefaults.OpenIdConnectScheme, oidc =>
            {
                oidc.TokenValidationParameters.NameClaimType = "name";
                oidc.TokenValidationParameters.RoleClaimType = "role";
                configureOidc(oidc);
            });
        services.AddOptions<CookieAuthenticationOptions>(OidcProxyDefaults.CookieScheme)
            .Configure<ProxyOptions>((cookie, options) => cookie.ExpireTimeSpan = options.SessionAbsoluteTimeout);
        services.AddOptions<OpenIdConnectOptions>(OidcProxyDefaults.OpenIdConnectScheme)
            .Configure<ProxyOptions>(ConfigureProtocolEvents)
            .Validate(o => Uri.TryCreate(o.Authority, UriKind.Absolute, out var authority) && authority.Scheme == "https" &&
                authority.UserInfo.Length == 0 && authority.Query.Length == 0 && authority.Fragment.Length == 0,
                "An HTTPS OIDC Authority without credentials, query or fragment is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId) && !string.IsNullOrWhiteSpace(o.ClientSecret),
                "A confidential OIDC ClientId and ClientSecret are required.")
            .ValidateOnStart();
    }

    private static void ConfigureProtocolEvents(OpenIdConnectOptions oidc, ProxyOptions options)
    {
        oidc.SignInScheme = OidcProxyDefaults.CookieScheme;
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
            context.ProtocolMessage.RedirectUri = new Uri(options.PublicOrigin!,
                context.Request.PathBase.Add(OidcProxyRoutes.LoginCallback).ToString()).AbsoluteUri;
        };
        oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = oidc.ClientId;
            context.ProtocolMessage.PostLogoutRedirectUri = new Uri(options.PublicOrigin!,
                context.Request.PathBase.Add(oidc.SignedOutCallbackPath).ToString()).AbsoluteUri;
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
            properties.Items.TryGetValue(BffConstants.PreviousSession, out var previousId);
            properties.Items.Remove(BffConstants.PreviousSession);
            var fullTicket = new AuthenticationTicket(context.Principal!, properties, OidcProxyDefaults.CookieScheme);
            string? id = null;
            try
            {
                id = await store.CreateAsync(fullTicket, context.HttpContext.RequestAborted);
                if (previousId is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var previousTicket = await store.TakeAsync(previousId, timeout.Token);
                    if (previousTicket is not null)
                        await services.GetRequiredService<TokenSession>().RevokeAsync(previousTicket, CancellationToken.None);
                }
                // The cookie receives only a framework-protected random reference.
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(BffConstants.SessionClaim, id)], OidcProxyDefaults.CookieScheme));
                properties.Items.Clear();
                properties.IssuedUtc = now;
                properties.ExpiresUtc = now + options.SessionAbsoluteTimeout;
                properties.IsPersistent = false;
            }
            catch
            {
                if (id is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await store.RemoveAsync(id, timeout.Token); }
                    catch (Exception e) when (e is RedisException or RedisTimeoutException or OperationCanceledException)
                    {
                        services.GetRequiredService<ILoggerFactory>().CreateLogger("MonixOne.OidcProxy.Yarp.Session")
                            .LogError("Unable to remove failed sign-in session. Failure type: {FailureType}", e.GetType().Name);
                    }
                }
                await services.GetRequiredService<TokenSession>().RevokeAsync(fullTicket, CancellationToken.None);
                throw;
            }
        };

        oidc.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
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
            return Task.CompletedTask;
        };
    }

}
