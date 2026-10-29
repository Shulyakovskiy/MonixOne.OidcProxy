using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Endpoints;

internal static class BffEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet(OidcProxyRoutes.Login, async (HttpContext context) =>
        {
            var properties = new AuthenticationProperties { RedirectUri = OidcProxyRoutes.ApplicationRoot };
            var previous = await context.AuthenticateAsync(OidcProxyDefaults.CookieScheme);
            if (context.Items.ContainsKey(typeof(SessionUnavailableException)))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            // A cross-site callback will not carry the Strict session cookie.
            // AuthenticationProperties are protected by the framework OIDC state.
            if (previous.Principal?.FindFirstValue(BffConstants.SessionClaim) is { } previousId)
                properties.Items[BffConstants.PreviousSession] = previousId;
            return Results.Challenge(properties, [OidcProxyDefaults.OpenIdConnectScheme]);
        }).AllowAnonymous();

        app.MapGet(OidcProxyRoutes.User, (HttpContext context) =>
            Results.Json(context.User.Claims.Where(c => c.Type != BffConstants.SessionClaim)
                .GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray())))
            .RequireAuthorization(BffConstants.AuthorizationPolicy).WithMetadata(BffConstants.ApiMetadata);

        app.MapGet(OidcProxyRoutes.Antiforgery, (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new { tokens.RequestToken, tokens.FormFieldName });
        }).RequireAuthorization(BffConstants.AuthorizationPolicy).WithMetadata(BffConstants.ApiMetadata);

        // Prevent a host SPA fallback from treating a logout link as a page.
        app.MapMethods(OidcProxyRoutes.Logout, [HttpMethods.Get, HttpMethods.Head], (HttpResponse response) =>
        {
            response.Headers.Allow = HttpMethods.Post;
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }).AllowAnonymous();

        app.MapPost(OidcProxyRoutes.Logout, async (HttpContext context, SessionStore store, TokenSession tokens) =>
        {
            var id = context.User.FindFirstValue(BffConstants.SessionClaim)!;
            // Once logout is authorized, a browser disconnect must not interrupt
            // its local invalidation. GETDEL returns the tokens current at deletion,
            // including a refresh that committed after authentication of this request.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ticket = await store.TakeAsync(id, timeout.Token);
            await context.SignOutAsync(OidcProxyDefaults.CookieScheme);
            if (ticket is not null)
            {
                context.Items[BffConstants.LogoutIdTokenItem] = ticket.Properties.GetTokenValue("id_token");
                await tokens.RevokeAsync(ticket, CancellationToken.None);
            }
            return Results.SignOut(new AuthenticationProperties { RedirectUri = OidcProxyRoutes.ApplicationRoot }, [OidcProxyDefaults.OpenIdConnectScheme]);
        }).RequireAuthorization(BffConstants.AuthorizationPolicy).WithMetadata(BffConstants.ApiMetadata);

    }
}
