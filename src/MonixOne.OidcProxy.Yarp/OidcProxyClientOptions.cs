namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Binds one confidential OIDC client to one exact public HTTPS origin.
/// Each SPA has its own origin, authentication schemes and server sessions.
/// </summary>
public sealed class OidcProxyClientOptions
{
    /// <summary>
    /// The exact public HTTPS origin shared by this SPA and its BFF endpoints.
    /// </summary>
    public Uri? PublicOrigin { get; set; }

    /// <summary>
    /// The optional URI-escaped deployment path, for example /portal.
    /// </summary>
    public string PathBase { get; set; } = string.Empty;

    /// <summary>
    /// The interval from the last accepted protected request. Activity extends
    /// idle expiry exactly through CAS and never extends the absolute lifetime.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The maximum session lifetime, which refresh never extends.
    /// </summary>
    public TimeSpan SessionAbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Forwards backend cookies with names isolated by client and YARP cluster.
    /// Backend paths are enforced during forwarding and expiry is retained.
    /// Browser cookies use __Host-Http-, Path=/, Secure and HttpOnly without Domain.
    /// No provider-specific names or paths are required.
    /// </summary>
    public bool ForwardCookies { get; set; }

    /// <summary>
    /// Selects routes by exact native YARP Hosts and its anonymous policy.
    /// Configuration registration enables this when Routes is omitted.
    /// Explicit bindings still take precedence.
    /// </summary>
    public bool AutoBindRoutes { get; set; }

    /// <summary>
    /// BFF behavior keyed by native YARP RouteId. Each route belongs to
    /// exactly one client. When omitted from configuration, AutoBindRoutes
    /// selects routes by exact YARP Hosts and the native anonymous policy.
    /// </summary>
    public Dictionary<string, OidcProxyRouteOptions> Routes { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Named, opt-in cookie policies referenced by this client's Routes.
    /// </summary>
    public Dictionary<string, OidcProxyCookiePolicy> CookiePolicies { get; set; } =
        new(StringComparer.Ordinal);

    internal TimeSpan RefreshTimeout => TimeSpan.FromSeconds(30);
    internal bool HasValidOrigin =>
        PublicOrigin is { IsAbsoluteUri: true, Scheme: "https", AbsolutePath: "/" } origin
        && origin.UserInfo.Length == 0
        && origin.Query.Length == 0
        && origin.Fragment.Length == 0;
    internal bool HasValidPathBase =>
        PathBase.Length == 0
        || PathBase.StartsWith('/')
            && !PathBase.StartsWith("//", StringComparison.Ordinal)
            && !PathBase.EndsWith('/')
            && Uri.TryCreate(PublicOrigin, PathBase, out var uri)
            && uri.AbsolutePath == PathBase
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;
}
