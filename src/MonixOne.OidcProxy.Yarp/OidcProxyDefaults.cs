namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Names used by standard ASP.NET Core authentication.
/// </summary>
public static class OidcProxyDefaults
{
    /// <summary>
    /// The credentialed CORS policy configured from Cors:Origins by AddOidcProxy.
    /// Apply it at the desired position in the host middleware callback.
    /// </summary>
    public const string CorsPolicy = "MonixOne.OidcProxy.Yarp.Cors";

    /// <summary>
    /// Returns the named cookie authentication scheme for a client.
    /// </summary>
    public static string CookieScheme(string clientName) =>
        $"MonixOne.OidcProxy.Yarp.{clientName}.Cookie";

    /// <summary>
    /// Returns the named OIDC authentication scheme for a client.
    /// </summary>
    public static string OpenIdConnectScheme(string clientName) =>
        $"MonixOne.OidcProxy.Yarp.{clientName}.OpenIdConnect";

    /// <summary>
    /// Returns the host-only session cookie name for a client.
    /// </summary>
    public static string CookieName(string clientName) =>
        $"__Host-Http-oidcproxy.{clientName}.session";
}
