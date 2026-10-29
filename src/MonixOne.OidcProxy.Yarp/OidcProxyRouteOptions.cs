namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Defines BFF behavior for one native YARP RouteId. Routing, destinations
/// and transport settings remain in the host's ReverseProxy configuration.
/// </summary>
public sealed class OidcProxyRouteOptions
{
    /// <summary>
    /// Allows requests without a BFF session, for example SPA assets or an
    /// external provider callback. Browser credentials are still removed.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Selects a cookie policy from the owning BFF client's CookiePolicies.
    /// </summary>
    public string? CookiePolicy { get; set; }
}
