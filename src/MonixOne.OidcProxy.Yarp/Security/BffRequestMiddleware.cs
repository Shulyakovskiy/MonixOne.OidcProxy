using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Yarp.ReverseProxy.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Security;

internal sealed class BffRequestMiddleware(RequestDelegate next)
{
    internal static void Register(IServiceCollection services)
    {
        services.AddAuthorization(authorization => authorization.AddPolicy(BffConstants.AuthorizationPolicy,
            policy => policy.RequireAuthenticatedUser()));
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = "__Host-Http-oidcproxy.session.csrf";
            antiforgery.Cookie.Path = "/";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Lax;
        });
    }

    public async Task InvokeAsync(HttpContext context, BffClients clients)
    {
        var endpoint = context.GetEndpoint();
        var client = endpoint?.Metadata.GetMetadata<BffClient>();
        var route = endpoint?.Metadata.GetMetadata<RouteModel>()?.Config;
        if (route is not null)
        {
            if (route.Metadata?.TryGetValue(OidcProxyDefaults.ClientMetadataKey, out var name) == true)
                clients.ByName.TryGetValue(name, out client);
            if (client is null)
            {
                if (clients.Find(context.Request) is not null)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
                return;
            }
        }
        if (client is null || route is null && endpoint?.Metadata.Contains(BffConstants.ApiMetadata) != true)
        {
            await next(context);
            return;
        }
        if (clients.Find(context.Request)?.Name != client.Name)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        // Explicit anonymous YARP routes serve assets/public data without a session.
        // Managed-route transforms remove BFF credentials; an explicit cookie
        // policy may carry separately namespaced application correlation cookies.
        if (route is not null && endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            if (route.Metadata?.ContainsKey(OidcProxyDefaults.CookiePolicyMetadataKey) == true) SetNoStore(context);
            await next(context);
            return;
        }
        var options = client.Options;
        SetNoStore(context);
        var origin = context.Request.Headers.Origin;
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"];
        if (origin.Count > 1 || origin.Count == 1 && origin[0] != options.PublicOrigin!.GetLeftPart(UriPartial.Authority) ||
            fetchSite.Count > 0 && fetchSite != "same-origin")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        // A principal established by another host middleware/scheme is not a BFF session.
        var authentication = await context.AuthenticateAsync(client.CookieScheme);
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
                context.Request.Path != OidcProxyRoutes.Logout || !context.Request.HasFormContentType)
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
            if (id is not null && context.Request.Path != OidcProxyRoutes.Logout &&
                context.Request.Path != OidcProxyRoutes.Antiforgery)
            {
                var ticket = await context.RequestServices.GetRequiredKeyedService<TokenSession>(client.Name)
                    .GetActiveTicketAsync(id, context.RequestAborted);
                if (ticket is null)
                {
                    await context.SignOutAsync(client.CookieScheme);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                context.Items[BffConstants.TicketItem] = ticket;
                context.User = SessionPrincipal.WithSessionId(ticket.Principal, id);
            }
            // Authorization must use this BFF session and its refreshed claims rather than
            // a cached result from the host's default Bearer/Identity authentication scheme.
            context.Features.Set<IAuthenticateResultFeature>(new BffAuthenticateResultFeature
            {
                AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(
                    context.User, authentication.Properties!, client.CookieScheme))
            });
            await next(context);
        }
        catch (Exception e) when (e is RedisException or RedisTimeoutException or SessionUnavailableException)
        {
            if (context.Response.HasStarted) throw;
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MonixOne.OidcProxy.Yarp.Session")
                .LogError("BFF session unavailable. Failure type: {FailureType}", e.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }
    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

}

internal sealed class BffAuthenticateResultFeature : IAuthenticateResultFeature
{
    public AuthenticateResult? AuthenticateResult { get; set; }
}
