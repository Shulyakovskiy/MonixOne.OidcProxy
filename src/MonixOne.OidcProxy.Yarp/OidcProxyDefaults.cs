namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Names used by standard ASP.NET Core authentication and YARP metadata.
/// </summary>
public static class OidcProxyDefaults
{
    /// <summary>
    /// The YARP route metadata key identifying a registered BFF client.
    /// </summary>
    public const string ClientMetadataKey = "OidcProxy.Client";
    /// <summary>
    /// The YARP route metadata key selecting one application cookie policy.
    /// </summary>
    public const string CookiePolicyMetadataKey = "OidcProxy.CookiePolicy";
    /// <summary>
    /// Returns the named cookie authentication scheme for a client.
    /// </summary>
    public static string CookieScheme(string clientName) => $"MonixOne.OidcProxy.Yarp.{clientName}.Cookie";
    /// <summary>
    /// Returns the named OIDC authentication scheme for a client.
    /// </summary>
    public static string OpenIdConnectScheme(string clientName) => $"MonixOne.OidcProxy.Yarp.{clientName}.OpenIdConnect";
    /// <summary>
    /// Returns the host-only session cookie name for a client.
    /// </summary>
    public static string CookieName(string clientName) => $"__Host-Http-oidcproxy.{clientName}.session";
}
