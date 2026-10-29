namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Identifies the standard named authentication options registered by the BFF.
/// </summary>
public static class OidcProxyDefaults
{
    /// <summary>
    /// The ASP.NET Core authentication scheme for the server session cookie.
    /// </summary>
    public const string CookieScheme = "MonixOne.OidcProxy.Yarp";

    /// <summary>
    /// The ASP.NET Core authentication scheme for the confidential OIDC client.
    /// </summary>
    public const string OpenIdConnectScheme = "MonixOne.OidcProxy.Yarp.OpenIdConnect";
}
