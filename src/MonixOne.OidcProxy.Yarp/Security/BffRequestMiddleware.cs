using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
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

    public async Task InvokeAsync(HttpContext context, ProxyOptions options)
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
        var authentication = await context.AuthenticateAsync(OidcProxyDefaults.CookieScheme);
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
                var ticket = await context.RequestServices.GetRequiredService<TokenSession>()
                    .GetActiveTicketAsync(id, context.RequestAborted);
                if (ticket is null)
                {
                    await context.SignOutAsync(OidcProxyDefaults.CookieScheme);
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
                    context.User, authentication.Properties!, OidcProxyDefaults.CookieScheme))
            });
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is RedisException or RedisTimeoutException or SessionUnavailableException)
        {
            if (context.Response.HasStarted) throw;
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MonixOne.OidcProxy.Yarp.Session")
                .LogError("BFF session unavailable. Failure type: {FailureType}", e.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }
}

internal sealed class BffAuthenticateResultFeature : IAuthenticateResultFeature
{
    public AuthenticateResult? AuthenticateResult { get; set; }
}
