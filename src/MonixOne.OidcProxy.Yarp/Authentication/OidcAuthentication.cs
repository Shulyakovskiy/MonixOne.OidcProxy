using System.Globalization;
using System.Security.Claims;
using Duende.IdentityModel.Client;
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
    /// <summary>
    /// Default identity-provider HTTP timeout. The host can override the standard
    /// OpenIdConnectOptions.BackchannelTimeout through its OIDC configuration.
    /// </summary>
    private static readonly TimeSpan DefaultBackchannelTimeout = TimeSpan.FromSeconds(10);

    internal static void Register(
        IServiceCollection services,
        string name,
        Action<OpenIdConnectOptions> configureOidc
    )
    {
        services
            .AddAuthentication()
            .AddCookie(
                OidcProxyDefaults.CookieScheme(name),
                cookie =>
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
                        if (
                            context
                                .HttpContext.RequestServices.GetRequiredService<BffClients>()
                                .Find(context.Request)
                                ?.Name != name
                        )
                        {
                            context.RejectPrincipal();
                            return;
                        }

                        var id = context.Principal?.FindFirstValue(BffConstants.SessionClaim);
                        try
                        {
                            var snapshot = id is null
                                ? null
                                : await context
                                    .HttpContext.RequestServices.GetRequiredKeyedService<SessionStore>(
                                        name
                                    )
                                    .ReadAsync(id, context.HttpContext.RequestAborted);
                            if (snapshot is null)
                                context.RejectPrincipal();
                            else
                            {
                                context.ReplacePrincipal(
                                    SessionPrincipal.WithSessionId(
                                        snapshot.State.ReadTicket().Principal,
                                        id!
                                    )
                                );
                                context.HttpContext.Items[BffConstants.SessionSnapshotItem] = (
                                    name,
                                    snapshot
                                );
                            }
                        }
                        catch (Exception e)
                            when (e
                                    is RedisException
                                        or RedisTimeoutException
                                        or SessionUnavailableException
                            )
                        {
                            context.HttpContext.Items[typeof(SessionUnavailableException)] = true;
                            context.RejectPrincipal();
                        }
                    };
                    cookie.Events.OnSigningOut = async context =>
                    {
                        var id = context.HttpContext.User.FindFirstValue(BffConstants.SessionClaim);
                        if (
                            id is not null
                            && !Equals(
                                context.HttpContext.Items[BffConstants.RemovedSessionItem],
                                (name, id)
                            )
                        )
                        {
                            using var timeout = new CancellationTokenSource(
                                context
                                    .HttpContext.RequestServices.GetRequiredService<BffClients>()
                                    .ByName[name]
                                    .Options.SessionStoreTimeout
                            );
                            try
                            {
                                await context
                                    .HttpContext.RequestServices.GetRequiredKeyedService<SessionStore>(
                                        name
                                    )
                                    .RemoveAsync(id, timeout.Token);
                            }
                            catch (OperationCanceledException e)
                                when (timeout.IsCancellationRequested)
                            {
                                throw new SessionUnavailableException(
                                    "Unable to confirm session invalidation within its server budget.",
                                    e
                                );
                            }
                        }
                    };
                }
            )
            .AddOpenIdConnect(
                OidcProxyDefaults.OpenIdConnectScheme(name),
                oidc =>
                {
                    oidc.BackchannelTimeout = DefaultBackchannelTimeout;
                    oidc.TokenValidationParameters.NameClaimType = "name";
                    oidc.TokenValidationParameters.RoleClaimType = "role";
                    configureOidc(oidc);
                }
            );
        services
            .AddOptions<CookieAuthenticationOptions>(OidcProxyDefaults.CookieScheme(name))
            .Configure<IOptionsMonitor<OidcProxyClientOptions>>(
                (cookie, options) =>
                    cookie.ExpireTimeSpan = options.Get(name).SessionAbsoluteTimeout
            );
        services
            .AddOptions<OpenIdConnectOptions>(OidcProxyDefaults.OpenIdConnectScheme(name))
            .Configure(oidc => ConfigureProtocolOptions(oidc, name))
            .PostConfigure<IOptionsMonitor<OidcProxyClientOptions>>(
                (oidc, options) => ConfigureProtocolEvents(oidc, name, options.Get(name))
            )
            .Validate(
                o => o.EventsType is null,
                "OpenIdConnect EventsType is not supported: configure Events callbacks so BFF session handling remains active."
            )
            .Validate(
                o =>
                    Uri.TryCreate(o.Authority, UriKind.Absolute, out var authority)
                    && authority
                        is {
                            Scheme: "https",
                            UserInfo.Length: 0,
                            Query.Length: 0,
                            Fragment.Length: 0
                        },
                "An HTTPS OIDC Authority without credentials, query or fragment is required."
            )
            .Validate(
                o =>
                    !string.IsNullOrWhiteSpace(o.ClientId)
                    && !string.IsNullOrWhiteSpace(o.ClientSecret),
                "A confidential OIDC ClientId and ClientSecret are required."
            )
            .ValidateOnStart();
    }

    private static void ConfigureProtocolOptions(OpenIdConnectOptions oidc, string name)
    {
        oidc.SignInScheme = OidcProxyDefaults.CookieScheme(name);
        oidc.SignedOutCallbackPath = OidcProxyRoutes.LogoutCallback;
        oidc.RemoteSignOutPath = PathString.Empty;
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
    }

    private static void ConfigureProtocolEvents(
        OpenIdConnectOptions oidc,
        string name,
        OidcProxyClientOptions options
    )
    {
        var pushAuthorization = oidc.Events.OnPushAuthorization;
        oidc.Events.OnPushAuthorization = async context =>
        {
            await pushAuthorization(context);
            if (
                options.ClientCredentialStyle == ClientCredentialStyle.PostBody
                || context.HandledClientAuthentication
                || context.HandledPush
                || context.SkippedPush
            )
                return;
            // .NET 10's native PAR uses POST credentials. IdentityModel supplies Basic
            // through the framework's supported PAR hook, without modifying a shared HttpClient.
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(
                context.HttpContext.RequestAborted
            );
            var response = await oidc.Backchannel.PushAuthorizationAsync(
                new PushedAuthorizationRequest
                {
                    Address = configuration.PushedAuthorizationRequestEndpoint,
                    ClientId = oidc.ClientId!,
                    ClientSecret = oidc.ClientSecret,
                    ClientCredentialStyle = options.ClientCredentialStyle,
                    Parameters = new Parameters(
                        context.ProtocolMessage.Parameters.Where(p =>
                            p.Key is not ("client_id" or "client_secret")
                        )
                    ),
                },
                context.HttpContext.RequestAborted
            );
            if (
                response.IsError
                || string.IsNullOrWhiteSpace(response.RequestUri)
                || response.ExpiresIn <= 0
            )
                throw new OpenIdConnectProtocolException(
                    "The identity provider rejected pushed authorization."
                );
            context.HandlePush(response.RequestUri);
        };
        var codeReceived = oidc.Events.OnAuthorizationCodeReceived;
        oidc.Events.OnAuthorizationCodeReceived = async context =>
        {
            await codeReceived(context);
            if (
                options.ClientCredentialStyle == ClientCredentialStyle.PostBody
                || context.HandledCodeRedemption
                || context.Result is not null
            )
                return;
            var request = context.TokenEndpointRequest!;
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(
                context.HttpContext.RequestAborted
            );
            // IdentityModel implements client_secret_basic; the native handler still
            // validates the complete token response, signature, state and nonce.
            var response = await context.Backchannel.RequestTokenAsync(
                new TokenRequest
                {
                    Address = configuration.TokenEndpoint,
                    GrantType = request.GrantType,
                    ClientId = request.ClientId!,
                    ClientSecret = request.ClientSecret,
                    ClientCredentialStyle = options.ClientCredentialStyle,
                    Parameters = new Parameters(
                        request.Parameters.Where(p =>
                            p.Key is not ("client_id" or "client_secret" or "grant_type")
                        )
                    ),
                },
                context.HttpContext.RequestAborted
            );
            if (response.IsError)
            {
                context.Fail("The identity provider rejected authorization code redemption.");
                return;
            }
            context.HandleCodeRedemption(new OpenIdConnectMessage(response.Raw!));
        };
        var validated = oidc.Events.OnTokenValidated;
        oidc.Events.OnTokenValidated = async context =>
        {
            // ASP.NET Core permits unsigned ID tokens from the HTTPS token endpoint
            // in code flow, as allowed by OIDC. This fork requires signed ID tokens.
            // The handler already verifies the signature whenever one is present.
            if (
                string.IsNullOrEmpty(
                    (context.SecurityToken.InnerToken ?? context.SecurityToken).RawSignature
                )
            )
            {
                context.Fail("An ID token with a validated signature is required.");
                return;
            }

            // The native handler skips protocol validation after manual code redemption.
            // Use its standard validator here for every code response, including Basic auth.
            if (context.TokenEndpointResponse is not null)
                oidc.ProtocolValidator.ValidateTokenResponse(
                    new OpenIdConnectProtocolValidationContext
                    {
                        ClientId = oidc.ClientId,
                        Nonce = context.Nonce,
                        ProtocolMessage = context.TokenEndpointResponse,
                        ValidatedIdToken = context.SecurityToken,
                    }
                );

            // Preserve protocol values before framework claim actions remove nonce.
            // The validated inner token also supports signed-and-encrypted ID tokens.
            var providerToken = context.SecurityToken.InnerToken ?? context.SecurityToken;
            context.Properties!.Items[BffConstants.ProviderIssuer] = providerToken.Issuer;
            context.Properties.Items[BffConstants.ProviderSubject] = providerToken.Subject;
            context.Properties.Items[BffConstants.ProviderSession] = providerToken
                .Claims.FirstOrDefault(claim => claim.Type == "sid")
                ?.Value;
            context.Properties!.Items[BffConstants.OriginalNonce] = context
                .SecurityToken
                .Payload
                .Nonce;
            if (context.SecurityToken.Payload.TryGetValue("auth_time", out var authTime))
                context.Properties.Items[BffConstants.OriginalAuthTime] = Convert.ToString(
                    authTime,
                    CultureInfo.InvariantCulture
                );
            await validated(context);
        };
        var redirect = oidc.Events.OnRedirectToIdentityProvider;
        oidc.Events.OnRedirectToIdentityProvider = async context =>
        {
            await redirect(context);
            context.ProtocolMessage.RedirectUri = UriHelper.BuildAbsolute(
                options.PublicOrigin!.Scheme,
                HostString.FromUriComponent(options.PublicOrigin.Authority),
                PathString.FromUriComponent(options.PathBase),
                OidcProxyRoutes.LoginCallback
            );
        };
        var signOutRedirect = oidc.Events.OnRedirectToIdentityProviderForSignOut;
        oidc.Events.OnRedirectToIdentityProviderForSignOut = async context =>
        {
            await signOutRedirect(context);
            context.ProtocolMessage.ClientId = oidc.ClientId;
            context.ProtocolMessage.PostLogoutRedirectUri = UriHelper.BuildAbsolute(
                options.PublicOrigin!.Scheme,
                HostString.FromUriComponent(options.PublicOrigin.Authority),
                PathString.FromUriComponent(options.PathBase),
                oidc.SignedOutCallbackPath
            );
            context.ProtocolMessage.IdTokenHint =
                context.HttpContext.Items[BffConstants.LogoutIdTokenItem] as string;
        };
        var tokenResponse = oidc.Events.OnTokenResponseReceived;
        oidc.Events.OnTokenResponseReceived = async context =>
        {
            // The provider has already issued credentials. Retain the known refresh
            // token only for this request so a later protocol failure can revoke it.
            if (!string.IsNullOrEmpty(context.TokenEndpointResponse.RefreshToken))
                context.HttpContext.Items[BffConstants.SignInRefreshTokenItem] = context
                    .TokenEndpointResponse
                    .RefreshToken;
            await tokenResponse(context);
            if (context.Result is not null)
            {
                return;
            }
            if (
                string.IsNullOrWhiteSpace(context.TokenEndpointResponse.AccessToken)
                || !string.Equals(
                    context.TokenEndpointResponse.TokenType,
                    "Bearer",
                    StringComparison.OrdinalIgnoreCase
                )
                || !long.TryParse(
                    context.TokenEndpointResponse.ExpiresIn,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var seconds
                )
                || seconds <= 0
                || seconds > int.MaxValue
            )
                context.Fail("Invalid token endpoint response.");
        };
        var ticketReceived = oidc.Events.OnTicketReceived;
        oidc.Events.OnTicketReceived = async context =>
        {
            var services = context.HttpContext.RequestServices;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            var store = services.GetRequiredKeyedService<SessionStore>(name);
            // These operations can outlive an aborted request or host shutdown.
            // Capture cleanup dependencies before starting server-owned Redis creation.
            var tokens = services.GetRequiredKeyedService<TokenSession>(name);
            var sessionLogger = services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("MonixOne.OidcProxy.Yarp.Session");
            var properties = context.Properties!;
            var remembered = properties.IsPersistent;
            var expiresAt =
                now
                + (remembered ? options.RememberedSessionTimeout : options.SessionAbsoluteTimeout);
            properties.Items.TryGetValue(BffConstants.PreviousSession, out var previousId);
            properties.Items.Remove(BffConstants.PreviousSession);
            var fullTicket = new AuthenticationTicket(
                context.Principal!,
                properties.Clone(),
                OidcProxyDefaults.CookieScheme(name)
            );
            // From here the ticket creation/cleanup block owns these credentials.
            context.HttpContext.Items.Remove(BffConstants.SignInRefreshTokenItem);
            string? id = null;
            try
            {
                await ticketReceived(context);
                if (context.Result is not null)
                {
                    await tokens.RevokeAsync(fullTicket, CancellationToken.None);
                    return;
                }
                // Host callbacks may replace properties. Keep validated credentials,
                // provider identity and server-owned expiry in the actual ticket.
                properties = context.Properties!;
                properties.StoreTokens(fullTicket.Properties.GetTokens());
                foreach (
                    var key in new[]
                    {
                        BffConstants.OriginalNonce,
                        BffConstants.OriginalAuthTime,
                        BffConstants.ProviderIssuer,
                        BffConstants.ProviderSubject,
                        BffConstants.ProviderSession,
                    }
                )
                {
                    if (fullTicket.Properties.Items.TryGetValue(key, out var value))
                        properties.Items[key] = value;
                }
                properties.IssuedUtc = now;
                properties.ExpiresUtc = expiresAt;
                properties.IsPersistent = remembered;
                fullTicket = new AuthenticationTicket(
                    context.Principal!,
                    properties,
                    OidcProxyDefaults.CookieScheme(name)
                );
                id = await store.CreateAsync(fullTicket, context.HttpContext.RequestAborted);
                context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                if (previousId is not null)
                {
                    using var timeout = new CancellationTokenSource(options.SessionStoreTimeout);
                    var previousTicket = await store.TakeAsync(previousId, timeout.Token);
                    if (previousTicket is not null)
                        await tokens.RevokeAsync(previousTicket, CancellationToken.None);
                }

                // The cookie receives only a framework-protected random reference.
                context.Principal = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(BffConstants.SessionClaim, id)],
                        OidcProxyDefaults.CookieScheme(name)
                    )
                );
                properties.Items.Clear();
                properties.IssuedUtc = now;
                properties.ExpiresUtc = expiresAt;
                properties.IsPersistent = remembered;
            }
            catch (Exception failure)
            {
                if (id is not null)
                {
                    using var timeout = new CancellationTokenSource(options.SessionStoreTimeout);
                    try
                    {
                        await store.RemoveAsync(id, timeout.Token);
                    }
                    catch (Exception e)
                        when (e
                                is RedisException
                                    or RedisTimeoutException
                                    or OperationCanceledException
                                    or ObjectDisposedException
                        )
                    {
                        sessionLogger.LogError(
                            "Unable to remove failed sign-in session. Failure type: {FailureType}",
                            e.GetType().Name
                        );
                    }
                }

                await tokens.RevokeAsync(fullTicket, CancellationToken.None);
                if (
                    failure
                    is RedisException
                        or RedisTimeoutException
                        or SessionUnavailableException
                )
                {
                    // TicketReceived runs after the native remote-authentication
                    // failure boundary. Handle infrastructure failures here too.
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.CacheControl = "no-store";
                    sessionLogger.LogWarning(
                        "OIDC session creation failed. Failure type: {FailureType}",
                        failure.GetType().Name
                    );
                    return;
                }

                throw;
            }
        };

        var remoteFailure = oidc.Events.OnRemoteFailure;
        oidc.Events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            if (
                context.HttpContext.Items.Remove(
                    BffConstants.SignInRefreshTokenItem,
                    out var issued
                ) && issued is string refreshToken
            )
            {
                await context
                    .HttpContext.RequestServices.GetRequiredKeyedService<TokenSession>(name)
                    .RevokeAsync(refreshToken, CancellationToken.None);
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            for (
                var failure = context.Failure;
                failure is not null;
                failure = failure.InnerException
            )
                if (
                    failure
                    is RedisException
                        or RedisTimeoutException
                        or SessionUnavailableException
                )
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    break;
                }

            context.Response.Headers.CacheControl = "no-store";
            context
                .HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MonixOne.OidcProxy.Yarp.Oidc")
                .LogWarning(
                    "OIDC sign-in failed. Failure type: {FailureType}",
                    context.Failure?.GetType().Name
                );
            await remoteFailure(context);
        };
    }
}
