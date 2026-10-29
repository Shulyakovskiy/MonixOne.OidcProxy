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
    /// The inactivity lifetime of the server session.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);
    /// <summary>
    /// The maximum session lifetime, which refresh never extends.
    /// </summary>
    public TimeSpan SessionAbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);
    /// <summary>
    /// Named, opt-in cookie policies referenced by YARP route metadata.
    /// </summary>
    public Dictionary<string, OidcProxyCookiePolicy> CookiePolicies { get; set; } = new(StringComparer.Ordinal);

    internal TimeSpan RefreshTimeout => TimeSpan.FromSeconds(30);
    internal bool HasValidOrigin => PublicOrigin is { IsAbsoluteUri: true, Scheme: "https", AbsolutePath: "/" } origin &&
        origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    internal bool HasValidPathBase => PathBase.Length == 0 || PathBase.StartsWith('/') &&
        !PathBase.StartsWith("//", StringComparison.Ordinal) && !PathBase.EndsWith('/') &&
        Uri.TryCreate(PublicOrigin, PathBase, out var uri) && uri.AbsolutePath == PathBase &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
