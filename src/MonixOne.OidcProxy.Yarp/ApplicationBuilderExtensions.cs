using Microsoft.AspNetCore.Builder;
using MonixOne.OidcProxy.Yarp.Endpoints;
using MonixOne.OidcProxy.Yarp.Security;

namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Composes the SPA BFF pipeline and maps its OIDC and YARP endpoints.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Enables BFF authentication, CSRF protection and token forwarding.
    /// Call after trusted forwarded headers and public static-file middleware.
    /// </summary>
    public static WebApplication UseOidcProxy(this WebApplication app)
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<BffRequestMiddleware>();
        app.UseAuthorization();
        BffEndpoints.Map(app);
        app.MapReverseProxy().RequireAuthorization(BffConstants.AuthorizationPolicy).WithMetadata(BffConstants.ApiMetadata);
        return app;
    }
}
