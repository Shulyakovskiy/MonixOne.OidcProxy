using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MonixOne.OidcProxy.Yarp.Endpoints;
using MonixOne.OidcProxy.Yarp.Security;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Composes the multi-origin SPA BFF pipeline and its YARP endpoints.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Enables host-bound OIDC, CSRF protection and token forwarding.
    /// Restores trusted forwarded headers before BFF processing.
    /// Call before static files and other routing.
    /// </summary>
    public static WebApplication UseOidcProxy(this WebApplication app) =>
        app.UseOidcProxy(configureBeforeAuthentication: null);

    /// <summary>
    /// Builds one YARP pipeline and invokes host middleware after forwarded
    /// headers, PathBase and routing, before authentication and BFF checks.
    /// Without configured clients, native gateway routing remains active.
    /// </summary>
    public static WebApplication UseOidcProxy(
        this WebApplication app,
        Action<WebApplication>? configureBeforeAuthentication
    )
    {
        ArgumentNullException.ThrowIfNull(app);
        var clients = app.Services.GetService<BffClients>();
        if (clients is not null)
            app.Services.GetRequiredService<ConnectionMultiplexer>();
        app.UseForwardedHeaders();
        if (clients is not null)
            app.Use(next =>
            {
                var mounts = clients
                    .ByName.Values.Where(c => c.Options.PathBase.Length > 0)
                    .ToDictionary(
                        c => c.Name,
                        c => new UsePathBaseMiddleware(
                            next,
                            PathString.FromUriComponent(c.Options.PathBase)
                        )
                    );
                return async context =>
                {
                    var client = clients.Find(context.Request);
                    if (client is not null && mounts.TryGetValue(client.Name, out var mount))
                    {
                        if (
                            !context.Request.Path.StartsWithSegments(
                                PathString.FromUriComponent(client.Options.PathBase)
                            )
                        )
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return;
                        }
                        await mount.Invoke(context);
                    }
                    else if (client is null && context.Request.Path.StartsWithSegments("/.auth"))
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                    else
                        await next(context);
                };
            });
        app.UseRouting();
        configureBeforeAuthentication?.Invoke(app);
        if (clients is not null)
            app.Use(
                async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    finally
                    {
                        // Host events may handle/skip the response at any OIDC stage.
                        // TicketReceived and RemoteFailure normally take ownership;
                        // clean up credentials still owned by an abandoned attempt.
                        if (
                            context.Items.Remove(
                                BffConstants.SignInRefreshTokenItem,
                                out var issued
                            )
                            && issued is string refreshToken
                            && clients.Find(context.Request) is { } client
                        )
                            await context
                                .RequestServices.GetRequiredKeyedService<TokenSession>(client.Name)
                                .RevokeAsync(refreshToken, CancellationToken.None);
                    }
                }
            );
        app.UseAuthentication();
        if (clients is not null)
            app.UseMiddleware<BffRequestMiddleware>();
        app.UseAuthorization();
        if (clients is not null)
            BffEndpoints.Map(app);
        app.MapReverseProxy();
        return app;
    }
}
