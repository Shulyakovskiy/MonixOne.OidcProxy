namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Defines the fixed same-origin endpoints of the SPA BFF.
/// </summary>
public static class OidcProxyRoutes
{
    /// <summary>
    /// Starts authorization code login through a browser navigation.
    /// </summary>
    public const string Login = "/.auth/login";
    /// <summary>
    /// Receives the OIDC authorization code callback.
    /// </summary>
    public const string LoginCallback = "/.auth/login/callback";
    /// <summary>
    /// Returns the authenticated session claims without OAuth tokens.
    /// </summary>
    public const string User = "/.auth/me";
    /// <summary>
    /// Issues the framework antiforgery token for native form logout.
    /// </summary>
    public const string Antiforgery = "/.auth/antiforgery";
    /// <summary>
    /// Ends the local session and initiates OIDC logout.
    /// </summary>
    public const string Logout = "/.auth/end-session";
    /// <summary>
    /// Receives the OIDC post-logout callback.
    /// </summary>
    public const string LogoutCallback = "/.auth/signout-callback";
    /// <summary>
    /// Returns the browser to the SPA entry point.
    /// </summary>
    public const string ApplicationRoot = "/";
}
