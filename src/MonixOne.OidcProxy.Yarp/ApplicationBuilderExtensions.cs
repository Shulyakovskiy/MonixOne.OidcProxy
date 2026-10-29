using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MonixOne.OidcProxy.Yarp.Endpoints;
using MonixOne.OidcProxy.Yarp.Security;

namespace MonixOne.OidcProxy.Yarp;

/// <summary>Composes the multi-origin SPA BFF pipeline and its YARP endpoints.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Enables host-bound OIDC, CSRF protection and token forwarding.
    /// Call after trusted forwarded headers, before static files and other routing.
    /// </summary>
    public static WebApplication UseOidcProxy(this WebApplication app)
    {
        var clients = app.Services.GetRequiredService<BffClients>();
        app.Use(next =>
        {
            var mounts = clients.ByName.Values.Where(c => c.Options.PathBase.Length > 0)
                .ToDictionary(c => c.Name, c => new UsePathBaseMiddleware(next, PathString.FromUriComponent(c.Options.PathBase)));
            return async context =>
            {
                var client = clients.Find(context.Request);
                if (client is not null && mounts.TryGetValue(client.Name, out var mount))
                {
                    if (!context.Request.Path.StartsWithSegments(PathString.FromUriComponent(client.Options.PathBase)))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    await mount.Invoke(context);
                }
                else if (client is null && context.Request.Path.StartsWithSegments("/.auth"))
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                else await next(context);
            };
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<BffRequestMiddleware>();
        app.UseAuthorization();
        BffEndpoints.Map(app);
        app.MapReverseProxy();
        return app;
    }
}
