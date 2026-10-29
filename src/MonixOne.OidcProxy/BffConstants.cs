namespace MonixOne.OidcProxy;

internal static class BffConstants
{
    internal const string CookieScheme = "MonixOne.OidcProxy";
    internal const string OidcScheme = "MonixOne.OidcProxy.OpenIdConnect";
    internal const string AuthorizationPolicy = "MonixOne.OidcProxy.Session";
    internal const string SessionClaim = "oidcproxy:session";
    internal static readonly object ApiMetadata = new();
    internal static readonly object TicketItem = new();
    internal static readonly object LogoutIdTokenItem = new();
}
