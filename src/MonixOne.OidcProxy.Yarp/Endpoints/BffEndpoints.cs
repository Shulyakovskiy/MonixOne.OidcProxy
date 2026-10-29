using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using MonixOne.OidcProxy.Yarp.Sessions;

namespace MonixOne.OidcProxy.Yarp.Endpoints;

internal static class BffEndpoints
{
    internal static void Map(WebApplication app)
    {
        foreach (var client in app.Services.GetRequiredService<BffClients>().ByName.Values)
            MapClient(app, client);
    }

    private static void MapClient(WebApplication app, BffClient client)
    {
        var options = client.Options;
        var routes = app.MapGroup(string.Empty)
            .RequireHost(options.PublicOrigin!.Authority)
            .WithMetadata(client);
        routes
            .MapGet(
                OidcProxyRoutes.Login,
                async (HttpContext context) =>
                {
                    var returnUrl = GetReturnUrl(context, options);
                    if (returnUrl is null)
                        return Results.BadRequest();
                    var properties = new AuthenticationProperties { RedirectUri = returnUrl };
                    var previous = await context.AuthenticateAsync(client.CookieScheme);
                    if (context.Items.ContainsKey(typeof(SessionUnavailableException)))
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    // A cross-site callback will not carry the Strict session cookie.
                    // AuthenticationProperties are protected by the framework OIDC state.
                    if (
                        previous.Principal?.FindFirstValue(BffConstants.SessionClaim) is
                        { } previousId
                    )
                        properties.Items[BffConstants.PreviousSession] = previousId;
                    return Results.Challenge(properties, [client.OpenIdConnectScheme]);
                }
            )
            .AllowAnonymous();

        routes
            .MapGet(
                OidcProxyRoutes.User,
                (HttpContext context) =>
                    Results.Json(
                        context
                            .User.Claims.Where(c => c.Type != BffConstants.SessionClaim)
                            .GroupBy(c => c.Type)
                            .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray())
                    )
            )
            .RequireAuthorization(BffConstants.AuthorizationPolicy)
            .WithMetadata(BffConstants.ApiMetadata);

        routes
            .MapGet(
                OidcProxyRoutes.Antiforgery,
                (HttpContext context, IAntiforgery antiforgery) =>
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    return Results.Json(new { tokens.RequestToken, tokens.FormFieldName });
                }
            )
            .RequireAuthorization(BffConstants.AuthorizationPolicy)
            .WithMetadata(BffConstants.ApiMetadata);

        // Prevent a host SPA fallback from treating a logout link as a page.
        routes
            .MapMethods(
                OidcProxyRoutes.Logout,
                [HttpMethods.Get, HttpMethods.Head],
                (HttpResponse response) =>
                {
                    response.Headers.Allow = HttpMethods.Post;
                    return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
                }
            )
            .AllowAnonymous();

        routes
            .MapPost(
                OidcProxyRoutes.Logout,
                async (HttpContext context) =>
                {
                    var returnUrl = GetReturnUrl(context, options);
                    if (returnUrl is null)
                        return Results.BadRequest();
                    var id = context.User.FindFirstValue(BffConstants.SessionClaim)!;
                    // Once logout is authorized, a browser disconnect must not interrupt
                    // its local invalidation. GETDEL returns the tokens current at deletion,
                    // including a refresh that committed after authentication of this request.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var store = context.RequestServices.GetRequiredKeyedService<SessionStore>(
                        client.Name
                    );
                    var tokens = context.RequestServices.GetRequiredKeyedService<TokenSession>(
                        client.Name
                    );
                    AuthenticationTicket? ticket;
                    try
                    {
                        ticket = await store.TakeAsync(id, timeout.Token);
                    }
                    catch (OperationCanceledException e) when (timeout.IsCancellationRequested)
                    {
                        throw new SessionUnavailableException(
                            "Unable to confirm local logout within its server budget.",
                            e
                        );
                    }
                    context.Items[BffConstants.RemovedSessionItem] = (client.Name, id);
                    await context.SignOutAsync(client.CookieScheme);
                    if (ticket is not null)
                    {
                        context.Items[BffConstants.LogoutIdTokenItem] =
                            ticket.Properties.GetTokenValue("id_token");
                        await tokens.RevokeAsync(ticket, CancellationToken.None);
                    }
                    return Results.SignOut(
                        new AuthenticationProperties { RedirectUri = returnUrl },
                        [client.OpenIdConnectScheme]
                    );
                }
            )
            .RequireAuthorization(BffConstants.AuthorizationPolicy)
            .WithMetadata(BffConstants.ApiMetadata);
    }

    private static string? GetReturnUrl(HttpContext context, OidcProxyClientOptions options)
    {
        var values = context.Request.Query[CookieAuthenticationDefaults.ReturnUrlParameter];
        if (values.Count == 0)
            return context.Request.PathBase.Add(OidcProxyRoutes.ApplicationRoot).ToString();
        if (values.Count != 1)
            return null;
        var candidate = values.ToString();
        if (!RedirectHttpResult.IsLocalUrl(candidate))
            return null;
        if (candidate.StartsWith("~/", StringComparison.Ordinal))
            candidate = context.Request.PathBase + candidate[1..];
        if (!Uri.TryCreate(options.PublicOrigin, candidate, out var target))
            return null;
        // Emit a canonical local URL, retaining query/fragment and URI escaping.
        return target.PathAndQuery + target.Fragment;
    }
}
