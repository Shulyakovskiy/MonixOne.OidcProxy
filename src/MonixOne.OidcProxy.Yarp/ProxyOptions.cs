namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Defines the public SPA origin and the scope/lifetime of its Redis sessions.
/// OIDC and routing use the standard ASP.NET Core and YARP options.
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>
    /// The exact public HTTPS origin shared by the SPA and BFF.
    /// </summary>
    public Uri? PublicOrigin { get; set; }

    /// <summary>
    /// The application segment used to isolate Redis and Data Protection keys.
    /// </summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>
    /// The deployment environment segment used to isolate Redis keys.
    /// </summary>
    public string EnvironmentName { get; set; } = string.Empty;

    /// <summary>
    /// The namespace distinguishing independent BFF clients in one environment.
    /// </summary>
    public string KeyNamespace { get; set; } = "oidcproxy";

    /// <summary>
    /// The inactivity lifetime of the server session.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The maximum lifetime of a session, which refresh never extends.
    /// </summary>
    public TimeSpan SessionAbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);

    internal string RedisKeyPrefix =>
        $"{ApplicationName.ToLowerInvariant()}:{EnvironmentName.ToLowerInvariant()}:{KeyNamespace.ToLowerInvariant()}:";

    internal string CookieName => "__Host-Http-oidcproxy.session";
    internal TimeSpan RefreshTimeout => TimeSpan.FromSeconds(30);
    internal TimeSpan RefreshBeforeExpiration => TimeSpan.FromSeconds(30);

    internal static bool IsKeySegment(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    internal bool HasValidOrigin =>
        PublicOrigin is { IsAbsoluteUri: true, Scheme: "https", AbsolutePath: "/" } origin &&
        origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0;
}